module OrgCli.Index.IndexDatabase

open System
open System.IO
open Microsoft.Data.Sqlite

let private createTablesSql =
    """
CREATE TABLE IF NOT EXISTS index_files (
    path TEXT PRIMARY KEY,
    hash TEXT NOT NULL,
    mtime INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS index_headlines (
    file TEXT NOT NULL,
    char_pos INTEGER NOT NULL,
    level INTEGER NOT NULL,
    title TEXT NOT NULL,
    todo TEXT,
    priority TEXT,
    scheduled TEXT,
    scheduled_dt TEXT,
    deadline TEXT,
    deadline_dt TEXT,
    closed TEXT,
    closed_dt TEXT,
    properties TEXT,
    body TEXT,
    outline_path TEXT,
    PRIMARY KEY (file, char_pos),
    FOREIGN KEY (file) REFERENCES index_files(path) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS index_headline_tags (
    file TEXT NOT NULL,
    char_pos INTEGER NOT NULL,
    tag TEXT NOT NULL,
    inherited INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (file, char_pos, tag),
    FOREIGN KEY (file, char_pos) REFERENCES index_headlines(file, char_pos) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_headlines_todo ON index_headlines(todo) WHERE todo IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_headlines_scheduled ON index_headlines(scheduled_dt) WHERE scheduled_dt IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_headlines_deadline ON index_headlines(deadline_dt) WHERE deadline_dt IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_headlines_closed ON index_headlines(closed_dt) WHERE closed_dt IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_headlines_outline ON index_headlines(outline_path);
"""

let private createFtsSql =
    """
CREATE VIRTUAL TABLE IF NOT EXISTS index_headlines_fts USING fts5(
    title,
    body,
    content=index_headlines,
    content_rowid=rowid
);
"""

type OrgIndexDb(dbPath: string) =
    let connectionString = sprintf "Data Source=%s;Foreign Keys=True" dbPath
    let mutable connection: SqliteConnection option = None
    let connectionLock = obj ()

    let ensureConnection () =
        lock connectionLock (fun () ->
            match connection with
            | Some conn when conn.State = Data.ConnectionState.Open -> conn
            | _ ->
                let conn = new SqliteConnection(connectionString)
                conn.Open()
                use walCmd = conn.CreateCommand()
                walCmd.CommandText <- "PRAGMA journal_mode=WAL"
                walCmd.ExecuteNonQuery() |> ignore
                use fkCmd = conn.CreateCommand()
                fkCmd.CommandText <- "PRAGMA foreign_keys=ON"
                fkCmd.ExecuteNonQuery() |> ignore
                connection <- Some conn
                conn)

    let executeNonQuery (sql: string) (parameters: (string * obj) list) =
        let conn = ensureConnection ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql

        for (name, value) in parameters do
            cmd.Parameters.AddWithValue(name, if isNull value then box DBNull.Value else value)
            |> ignore

        cmd.ExecuteNonQuery()

    let executeScalarObj (sql: string) (parameters: (string * obj) list) : obj option =
        let conn = ensureConnection ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql

        for (name, value) in parameters do
            cmd.Parameters.AddWithValue(name, if isNull value then box DBNull.Value else value)
            |> ignore

        let result = cmd.ExecuteScalar()

        if isNull result || result = box DBNull.Value then
            None
        else
            Some result

    let executeReader (sql: string) (parameters: (string * obj) list) (mapper: SqliteDataReader -> 'T) : 'T list =
        let conn = ensureConnection ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql

        for (name, value) in parameters do
            cmd.Parameters.AddWithValue(name, if isNull value then box DBNull.Value else value)
            |> ignore

        use reader = cmd.ExecuteReader()
        let results = ResizeArray<'T>()

        while reader.Read() do
            results.Add(mapper reader)

        results |> Seq.toList

    let readOptString (r: SqliteDataReader) (i: int) =
        if r.IsDBNull(i) then None else Some(r.GetString(i))

    member _.Initialize() =
        let conn = ensureConnection ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- createTablesSql
        cmd.ExecuteNonQuery() |> ignore
        use ftsCmd = conn.CreateCommand()
        ftsCmd.CommandText <- createFtsSql
        ftsCmd.ExecuteNonQuery() |> ignore

    member _.Close() =
        lock connectionLock (fun () ->
            match connection with
            | Some conn ->
                conn.Close()
                conn.Dispose()
                connection <- None
            | None -> ())

    member _.BeginTransaction() =
        let conn = ensureConnection ()
        conn.BeginTransaction()

    // ── Files ──

    member _.InsertFile(file: IndexedFile) =
        executeNonQuery
            "INSERT OR REPLACE INTO index_files (path, hash, mtime) VALUES (@path, @hash, @mtime)"
            [ "@path", box file.Path; "@hash", box file.Hash; "@mtime", box file.Mtime ]
        |> ignore

    member _.GetFileHash(path: string) : string option =
        executeScalarObj "SELECT hash FROM index_files WHERE path = @path" [ "@path", box path ]
        |> Option.map (fun o -> Convert.ToString(o))

    member _.GetFile(path: string) : IndexedFile option =
        executeReader "SELECT path, hash, mtime FROM index_files WHERE path = @path" [ "@path", box path ] (fun r ->
            { Path = r.GetString(0)
              Hash = r.GetString(1)
              Mtime = r.GetInt64(2) })
        |> List.tryHead

    member _.GetAllFiles() : IndexedFile list =
        executeReader "SELECT path, hash, mtime FROM index_files" [] (fun r ->
            { Path = r.GetString(0)
              Hash = r.GetString(1)
              Mtime = r.GetInt64(2) })

    member _.DeleteFile(path: string) =
        executeNonQuery "DELETE FROM index_files WHERE path = @path" [ "@path", box path ]
        |> ignore

    member _.UpdateFileMtime(path: string, mtime: int64) =
        executeNonQuery "UPDATE index_files SET mtime = @mtime WHERE path = @path" [ "@path", box path; "@mtime", box mtime ]
        |> ignore

    member _.UpdateFile(path: string, hash: string, mtime: int64) =
        executeNonQuery
            "UPDATE index_files SET hash = @hash, mtime = @mtime WHERE path = @path"
            [ "@path", box path; "@hash", box hash; "@mtime", box mtime ]
        |> ignore

    // ── Headlines ──

    member _.InsertHeadline(h: IndexedHeadline) =
        let sql =
            """
            INSERT INTO index_headlines (file, char_pos, level, title, todo, priority,
                scheduled, scheduled_dt, deadline, deadline_dt, closed, closed_dt,
                properties, body, outline_path)
            VALUES (@file, @charPos, @level, @title, @todo, @priority,
                @scheduled, @scheduledDt, @deadline, @deadlineDt, @closed, @closedDt,
                @properties, @body, @outlinePath)
        """

        let optObj (v: string option) : obj =
            match v with
            | Some s -> box s
            | None -> box DBNull.Value

        executeNonQuery
            sql
            [ "@file", box h.File
              "@charPos", box h.CharPos
              "@level", box h.Level
              "@title", box h.Title
              "@todo", optObj h.Todo
              "@priority", optObj h.Priority
              "@scheduled", optObj h.Scheduled
              "@scheduledDt", optObj h.ScheduledDt
              "@deadline", optObj h.Deadline
              "@deadlineDt", optObj h.DeadlineDt
              "@closed", optObj h.Closed
              "@closedDt", optObj h.ClosedDt
              "@properties", optObj h.Properties
              "@body", optObj h.Body
              "@outlinePath", optObj h.OutlinePath ]
        |> ignore

    member _.GetHeadlines(file: string) : IndexedHeadline list =
        let sql =
            """
            SELECT file, char_pos, level, title, todo, priority,
                   scheduled, scheduled_dt, deadline, deadline_dt, closed, closed_dt,
                   properties, body, outline_path
            FROM index_headlines WHERE file = @file ORDER BY char_pos
        """

        executeReader sql [ "@file", box file ] (fun r ->
            { File = r.GetString(0)
              CharPos = r.GetInt64(1)
              Level = r.GetInt32(2)
              Title = r.GetString(3)
              Todo = readOptString r 4
              Priority = readOptString r 5
              Scheduled = readOptString r 6
              ScheduledDt = readOptString r 7
              Deadline = readOptString r 8
              DeadlineDt = readOptString r 9
              Closed = readOptString r 10
              ClosedDt = readOptString r 11
              Properties = readOptString r 12
              Body = readOptString r 13
              OutlinePath = readOptString r 14 })

    member _.GetHeadline(file: string, charPos: int64) : IndexedHeadline option =
        let sql =
            """
            SELECT file, char_pos, level, title, todo, priority,
                   scheduled, scheduled_dt, deadline, deadline_dt, closed, closed_dt,
                   properties, body, outline_path
            FROM index_headlines WHERE file = @file AND char_pos = @charPos
        """

        executeReader sql [ "@file", box file; "@charPos", box charPos ] (fun r ->
            { File = r.GetString(0)
              CharPos = r.GetInt64(1)
              Level = r.GetInt32(2)
              Title = r.GetString(3)
              Todo = readOptString r 4
              Priority = readOptString r 5
              Scheduled = readOptString r 6
              ScheduledDt = readOptString r 7
              Deadline = readOptString r 8
              DeadlineDt = readOptString r 9
              Closed = readOptString r 10
              ClosedDt = readOptString r 11
              Properties = readOptString r 12
              Body = readOptString r 13
              OutlinePath = readOptString r 14 })
        |> List.tryHead

    member _.DeleteHeadlines(file: string) =
        executeNonQuery "DELETE FROM index_headlines WHERE file = @file" [ "@file", box file ]
        |> ignore

    // ── Tags ──

    member _.InsertTag(tag: IndexedTag) =
        executeNonQuery
            "INSERT INTO index_headline_tags (file, char_pos, tag, inherited) VALUES (@file, @charPos, @tag, @inherited)"
            [ "@file", box tag.File
              "@charPos", box tag.CharPos
              "@tag", box tag.Tag
              "@inherited", box (if tag.Inherited then 1 else 0) ]
        |> ignore

    member _.InsertTagIgnore(tag: IndexedTag) =
        executeNonQuery
            "INSERT OR IGNORE INTO index_headline_tags (file, char_pos, tag, inherited) VALUES (@file, @charPos, @tag, @inherited)"
            [ "@file", box tag.File
              "@charPos", box tag.CharPos
              "@tag", box tag.Tag
              "@inherited", box (if tag.Inherited then 1 else 0) ]
        |> ignore

    member _.GetTags(file: string, charPos: int64) : IndexedTag list =
        executeReader
            "SELECT file, char_pos, tag, inherited FROM index_headline_tags WHERE file = @file AND char_pos = @charPos"
            [ "@file", box file; "@charPos", box charPos ]
            (fun r ->
                { File = r.GetString(0)
                  CharPos = r.GetInt64(1)
                  Tag = r.GetString(2)
                  Inherited = r.GetInt32(3) <> 0 })

    member _.GetTagsByName(tag: string) : IndexedTag list =
        executeReader
            "SELECT file, char_pos, tag, inherited FROM index_headline_tags WHERE tag = @tag"
            [ "@tag", box tag ]
            (fun r ->
                { File = r.GetString(0)
                  CharPos = r.GetInt64(1)
                  Tag = r.GetString(2)
                  Inherited = r.GetInt32(3) <> 0 })

    // ── FTS ──

    member _.DeleteFtsForFile(file: string) =
        executeNonQuery
            "INSERT INTO index_headlines_fts(index_headlines_fts, rowid, title, body) SELECT 'delete', rowid, title, body FROM index_headlines WHERE file = @file"
            [ "@file", box file ]
        |> ignore

    member _.RebuildFtsForFile(file: string) =
        executeNonQuery
            "INSERT INTO index_headlines_fts(rowid, title, body) SELECT rowid, title, body FROM index_headlines WHERE file = @file"
            [ "@file", box file ]
        |> ignore

    member _.SearchFts(query: string) : FtsResult list =
        let sql =
            """
            SELECT h.file, h.char_pos, h.title, h.outline_path,
                   snippet(index_headlines_fts, -1, '>>>', '<<<', '...', 32) as context,
                   rank
            FROM index_headlines_fts
            JOIN index_headlines h ON index_headlines_fts.rowid = h.rowid
            WHERE index_headlines_fts MATCH @query
            ORDER BY rank
        """

        executeReader sql [ "@query", box query ] (fun r ->
            { File = r.GetString(0)
              CharPos = r.GetInt64(1)
              Title = r.GetString(2)
              OutlinePath = readOptString r 3
              Context = readOptString r 4
              Rank = r.GetDouble(5) })

    // ── Query ──

    member _.QueryHeadlines(?todo: string, ?tag: string, ?outlinePathPrefix: string) : HeadlineQueryResult list =
        let mutable conditions = ResizeArray<string>()
        let mutable parameters = ResizeArray<string * obj>()
        let mutable needsTagJoin = false

        match todo with
        | Some t ->
            conditions.Add("h.todo = @todo")
            parameters.Add("@todo", box t)
        | None -> ()

        match tag with
        | Some t ->
            needsTagJoin <- true
            conditions.Add("t.tag = @tag")
            parameters.Add("@tag", box t)
        | None -> ()

        match outlinePathPrefix with
        | Some p ->
            // LIKE 'prefix' || X'1F' || '%'
            let likePattern = p + "\x1F" + "%"
            conditions.Add("h.outline_path LIKE @outlinePrefix")
            parameters.Add("@outlinePrefix", box likePattern)
        | None -> ()

        let joinClause =
            if needsTagJoin then
                "JOIN index_headline_tags t ON h.file = t.file AND h.char_pos = t.char_pos"
            else
                ""

        let whereClause =
            if conditions.Count > 0 then
                "WHERE " + String.Join(" AND ", conditions)
            else
                ""

        let sql =
            sprintf
                "SELECT DISTINCT h.file, h.char_pos, h.title, h.level, h.todo, h.priority, h.scheduled, h.deadline, h.closed, h.outline_path, h.properties FROM index_headlines h %s %s ORDER BY h.file, h.char_pos"
                joinClause
                whereClause

        executeReader sql (parameters |> Seq.toList) (fun r ->
            { File = r.GetString(0)
              CharPos = r.GetInt64(1)
              Title = r.GetString(2)
              Level = r.GetInt32(3)
              Todo = readOptString r 4
              Priority = readOptString r 5
              Scheduled = readOptString r 6
              Deadline = readOptString r 7
              Closed = readOptString r 8
              OutlinePath = readOptString r 9
              Properties = readOptString r 10 })

    member _.QueryAgendaNonRepeating(startDate: string, endDate: string) : AgendaQueryResult list =
        let sql =
            """
            SELECT file, char_pos, title, level, todo, priority,
                   scheduled, scheduled_dt, deadline, deadline_dt, outline_path
            FROM index_headlines
            WHERE (scheduled_dt BETWEEN @start AND @end AND (scheduled IS NULL OR scheduled NOT LIKE '%+%'))
               OR (deadline_dt BETWEEN @start AND @end AND (deadline IS NULL OR deadline NOT LIKE '%+%'))
        """

        executeReader sql [ "@start", box startDate; "@end", box endDate ] (fun r ->
            { File = r.GetString(0)
              CharPos = r.GetInt64(1)
              Title = r.GetString(2)
              Level = r.GetInt32(3)
              Todo = readOptString r 4
              Priority = readOptString r 5
              Scheduled = readOptString r 6
              ScheduledDt = readOptString r 7
              Deadline = readOptString r 8
              DeadlineDt = readOptString r 9
              OutlinePath = readOptString r 10 })

    member _.QueryAgendaRepeating() : AgendaQueryResult list =
        let sql =
            """
            SELECT file, char_pos, title, level, todo, priority,
                   scheduled, scheduled_dt, deadline, deadline_dt, outline_path
            FROM index_headlines
            WHERE (scheduled IS NOT NULL AND scheduled LIKE '%+%')
               OR (deadline IS NOT NULL AND deadline LIKE '%+%')
        """

        executeReader sql [] (fun r ->
            { File = r.GetString(0)
              CharPos = r.GetInt64(1)
              Title = r.GetString(2)
              Level = r.GetInt32(3)
              Todo = readOptString r 4
              Priority = readOptString r 5
              Scheduled = readOptString r 6
              ScheduledDt = readOptString r 7
              Deadline = readOptString r 8
              DeadlineDt = readOptString r 9
              OutlinePath = readOptString r 10 })

    member _.ExecuteScalarInt64(sql: string) : int64 =
        let conn = ensureConnection ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        Convert.ToInt64(cmd.ExecuteScalar())

    member _.ExecuteScalarString(sql: string) : string =
        let conn = ensureConnection ()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        Convert.ToString(cmd.ExecuteScalar())

    member _.ExecuteInTransaction(action: unit -> unit) =
        executeNonQuery "BEGIN IMMEDIATE" [] |> ignore

        try
            action ()
            executeNonQuery "COMMIT" [] |> ignore
        with ex ->
            try
                executeNonQuery "ROLLBACK" [] |> ignore
            with _ ->
                ()

            raise ex

    interface IDisposable with
        member this.Dispose() =
            try
                this.Close()
            with _ ->
                ()
