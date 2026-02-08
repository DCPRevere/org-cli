module OrgCli.Roam.Database

open System
open System.IO
open Microsoft.Data.Sqlite
open OrgCli.Roam

/// SQL schema for org-roam v20 database
let private createTablesSql =
    """
CREATE TABLE IF NOT EXISTS files (
    file TEXT PRIMARY KEY UNIQUE,
    title TEXT,
    hash TEXT NOT NULL,
    atime TEXT NOT NULL,
    mtime TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS nodes (
    id TEXT PRIMARY KEY NOT NULL,
    file TEXT NOT NULL,
    level INTEGER NOT NULL,
    pos INTEGER NOT NULL,
    todo TEXT,
    priority TEXT,
    scheduled TEXT,
    deadline TEXT,
    title TEXT,
    properties TEXT,
    olp TEXT,
    FOREIGN KEY (file) REFERENCES files(file) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS aliases (
    node_id TEXT NOT NULL,
    alias TEXT,
    FOREIGN KEY (node_id) REFERENCES nodes(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS citations (
    node_id TEXT NOT NULL,
    cite_key TEXT NOT NULL,
    pos INTEGER NOT NULL,
    properties TEXT,
    FOREIGN KEY (node_id) REFERENCES nodes(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS refs (
    node_id TEXT NOT NULL,
    ref TEXT NOT NULL,
    type TEXT NOT NULL,
    FOREIGN KEY (node_id) REFERENCES nodes(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS tags (
    node_id TEXT NOT NULL,
    tag TEXT,
    FOREIGN KEY (node_id) REFERENCES nodes(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS links (
    pos INTEGER NOT NULL,
    source TEXT NOT NULL,
    dest TEXT NOT NULL,
    type TEXT NOT NULL,
    properties TEXT NOT NULL,
    FOREIGN KEY (source) REFERENCES nodes(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS alias_node_id ON aliases(node_id);
CREATE INDEX IF NOT EXISTS refs_node_id ON refs(node_id);
CREATE INDEX IF NOT EXISTS tags_node_id ON tags(node_id);
"""

/// Database connection wrapper
type OrgRoamDb(dbPath: string) =
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

    let executeScalarString (sql: string) (parameters: (string * obj) list) : string option =
        executeScalarObj sql parameters |> Option.map (fun o -> Convert.ToString(o))

    let executeScalarInt64 (sql: string) (parameters: (string * obj) list) : int64 option =
        executeScalarObj sql parameters |> Option.map (fun o -> Convert.ToInt64(o))

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

    member _.Initialize() : Result<unit, string> =
        let isNewDb = not (File.Exists(dbPath))
        let dir = Path.GetDirectoryName(dbPath)

        if not (String.IsNullOrEmpty(dir)) && not (Directory.Exists(dir)) then
            Directory.CreateDirectory(dir) |> ignore

        let conn = ensureConnection ()

        let createAndSetVersion () =
            use cmd = conn.CreateCommand()
            cmd.CommandText <- createTablesSql
            cmd.ExecuteNonQuery() |> ignore

            use versionCmd = conn.CreateCommand()
            versionCmd.CommandText <- sprintf "PRAGMA user_version = %d" Domain.DbVersion
            versionCmd.ExecuteNonQuery() |> ignore
            Ok()

        if isNewDb then
            createAndSetVersion ()
        else
            use versionCmd = conn.CreateCommand()
            versionCmd.CommandText <- "PRAGMA user_version"
            let version = versionCmd.ExecuteScalar() |> System.Convert.ToInt64 |> int

            if version = 0 then
                // DB exists but roam was never initialized (e.g., created by the index module).
                createAndSetVersion ()
            elif version > Domain.DbVersion then
                Error "Database was created with a newer version of org-roam"
            elif version < Domain.DbVersion then
                Error(
                    sprintf
                        "Database has older schema version %d (expected %d). Delete the database and re-sync."
                        version
                        Domain.DbVersion
                )
            else
                Ok()

    member _.Close() =
        match connection with
        | Some conn ->
            conn.Close()
            conn.Dispose()
            connection <- None
        | None -> ()

    member _.BeginTransaction() =
        let conn = ensureConnection ()
        conn.BeginTransaction()

    // File operations
    member _.InsertFile(file: DbFile) =
        let sql =
            """
            INSERT INTO files (file, title, hash, atime, mtime)
            VALUES (@file, @title, @hash, @atime, @mtime)
        """

        let atimeStr = file.Atime.ToString("o")
        let mtimeStr = file.Mtime.ToString("o")

        executeNonQuery
            sql
            [ "@file", box file.File
              "@title", box (file.Title |> Option.toObj)
              "@hash", box file.Hash
              "@atime", box atimeStr
              "@mtime", box mtimeStr ]
        |> ignore

    member _.GetFileHash(filePath: string) : string option =
        executeScalarString "SELECT hash FROM files WHERE file = @file" [ "@file", box filePath ]

    member _.ClearFile(filePath: string) =
        executeNonQuery "DELETE FROM files WHERE file = @file" [ "@file", box filePath ]
        |> ignore

    member _.GetAllFileHashes() : Map<string, string> =
        let sql = "SELECT file, hash FROM files"
        executeReader sql [] (fun r -> r.GetString(0), r.GetString(1)) |> Map.ofList

    // Node operations
    member _.InsertNode(node: DbNode) =
        let sql =
            """
            INSERT INTO nodes (id, file, level, pos, todo, priority, scheduled, deadline, title, properties, olp)
            VALUES (@id, @file, @level, @pos, @todo, @priority, @scheduled, @deadline, @title, @properties, @olp)
        """

        executeNonQuery
            sql
            [ "@id", box node.Id
              "@file", box node.File
              "@level", box node.Level
              "@pos", box node.Pos
              "@todo", box (node.Todo |> Option.toObj)
              "@priority", box (node.Priority |> Option.toObj)
              "@scheduled", box (node.Scheduled |> Option.toObj)
              "@deadline", box (node.Deadline |> Option.toObj)
              "@title", box node.Title
              "@properties", box node.Properties
              "@olp", box node.Olp ]
        |> ignore

    member _.GetNode(nodeId: string) : DbNode option =
        let sql =
            """
            SELECT id, file, level, pos, todo, priority, scheduled, deadline, title, properties, olp
            FROM nodes WHERE id = @id LIMIT 1
        """

        executeReader sql [ "@id", box nodeId ] (fun r ->
            { Id = r.GetString(0)
              File = r.GetString(1)
              Level = r.GetInt32(2)
              Pos = r.GetInt32(3)
              Todo = if r.IsDBNull(4) then None else Some(r.GetString(4))
              Priority = if r.IsDBNull(5) then None else Some(r.GetString(5))
              Scheduled = if r.IsDBNull(6) then None else Some(r.GetString(6))
              Deadline = if r.IsDBNull(7) then None else Some(r.GetString(7))
              Title = r.GetString(8)
              Properties = if r.IsDBNull(9) then "" else r.GetString(9)
              Olp = if r.IsDBNull(10) then "" else r.GetString(10) })
        |> List.tryHead

    member this.GetNodeCount(nodeId: string) : int =
        executeScalarInt64 "SELECT COUNT(*) FROM nodes WHERE id = @id" [ "@id", box nodeId ]
        |> Option.map int
        |> Option.defaultValue 0

    member _.GetAllNodes() : DbNode list =
        let sql =
            """
            SELECT id, file, level, pos, todo, priority, scheduled, deadline, title, properties, olp
            FROM nodes
        """

        executeReader sql [] (fun r ->
            { Id = r.GetString(0)
              File = r.GetString(1)
              Level = r.GetInt32(2)
              Pos = r.GetInt32(3)
              Todo = if r.IsDBNull(4) then None else Some(r.GetString(4))
              Priority = if r.IsDBNull(5) then None else Some(r.GetString(5))
              Scheduled = if r.IsDBNull(6) then None else Some(r.GetString(6))
              Deadline = if r.IsDBNull(7) then None else Some(r.GetString(7))
              Title = r.GetString(8)
              Properties = if r.IsDBNull(9) then "" else r.GetString(9)
              Olp = if r.IsDBNull(10) then "" else r.GetString(10) })

    // Alias operations
    member _.InsertAlias(alias: DbAlias) =
        let sql = "INSERT INTO aliases (node_id, alias) VALUES (@nodeId, @alias)"

        executeNonQuery sql [ "@nodeId", box alias.NodeId; "@alias", box alias.Alias ]
        |> ignore

    member _.GetAliases(nodeId: string) : string list =
        executeReader "SELECT alias FROM aliases WHERE node_id = @nodeId" [ "@nodeId", box nodeId ] (fun r ->
            r.GetString(0))

    // Tag operations
    member _.InsertTag(tag: DbTag) =
        let sql = "INSERT INTO tags (node_id, tag) VALUES (@nodeId, @tag)"
        executeNonQuery sql [ "@nodeId", box tag.NodeId; "@tag", box tag.Tag ] |> ignore

    member _.GetTags(nodeId: string) : string list =
        executeReader "SELECT tag FROM tags WHERE node_id = @nodeId" [ "@nodeId", box nodeId ] (fun r -> r.GetString(0))

    member _.GetAllTags() : string list =
        executeReader "SELECT DISTINCT tag FROM tags" [] (fun r -> r.GetString(0))

    // Ref operations
    member _.InsertRef(ref: DbRef) =
        let sql = "INSERT INTO refs (node_id, ref, type) VALUES (@nodeId, @ref, @type)"

        executeNonQuery sql [ "@nodeId", box ref.NodeId; "@ref", box ref.Ref; "@type", box ref.Type ]
        |> ignore

    member _.GetRefs(nodeId: string) : (string * string) list =
        executeReader "SELECT ref, type FROM refs WHERE node_id = @nodeId" [ "@nodeId", box nodeId ] (fun r ->
            r.GetString(0), r.GetString(1))

    // Link operations
    member _.InsertLink(link: DbLink) =
        let sql =
            """
            INSERT INTO links (pos, source, dest, type, properties)
            VALUES (@pos, @source, @dest, @type, @properties)
        """

        executeNonQuery
            sql
            [ "@pos", box link.Pos
              "@source", box link.Source
              "@dest", box link.Dest
              "@type", box link.Type
              "@properties", box link.Properties ]
        |> ignore

    member _.GetLinksFrom(nodeId: string) : DbLink list =
        let sql =
            "SELECT pos, source, dest, type, properties FROM links WHERE source = @source"

        executeReader sql [ "@source", box nodeId ] (fun r ->
            { Pos = r.GetInt32(0)
              Source = r.GetString(1)
              Dest = r.GetString(2)
              Type = r.GetString(3)
              Properties = r.GetString(4) })

    member _.GetLinksTo(nodeId: string) : DbLink list =
        let sql = "SELECT pos, source, dest, type, properties FROM links WHERE dest = @dest"

        executeReader sql [ "@dest", box nodeId ] (fun r ->
            { Pos = r.GetInt32(0)
              Source = r.GetString(1)
              Dest = r.GetString(2)
              Type = r.GetString(3)
              Properties = r.GetString(4) })

    // Citation operations
    member _.InsertCitation(citation: DbCitation) =
        let sql =
            """
            INSERT INTO citations (node_id, cite_key, pos, properties)
            VALUES (@nodeId, @citeKey, @pos, @properties)
        """

        executeNonQuery
            sql
            [ "@nodeId", box citation.NodeId
              "@citeKey", box citation.CiteKey
              "@pos", box citation.Pos
              "@properties", box citation.Properties ]
        |> ignore

    // Query operations
    member this.FindNodeByTitleOrAlias(searchTerm: string) : DbNode option =
        // First try exact title match
        let sql = "SELECT id FROM nodes WHERE title = @term LIMIT 1"
        let byTitle = executeScalarString sql [ "@term", box searchTerm ]

        match byTitle with
        | Some id -> this.GetNode(id)
        | None ->
            // Try alias match
            let aliasSql = "SELECT node_id FROM aliases WHERE alias = @term LIMIT 1"
            let byAlias = executeScalarString aliasSql [ "@term", box searchTerm ]

            match byAlias with
            | Some id -> this.GetNode(id)
            | None -> None

    member _.FindNodesByTag(tag: string) : DbNode list =
        let sql =
            """
            SELECT n.id, n.file, n.level, n.pos, n.todo, n.priority, n.scheduled, n.deadline, n.title, n.properties, n.olp
            FROM nodes n
            INNER JOIN tags t ON n.id = t.node_id
            WHERE t.tag = @tag
        """

        executeReader sql [ "@tag", box tag ] (fun r ->
            { Id = r.GetString(0)
              File = r.GetString(1)
              Level = r.GetInt32(2)
              Pos = r.GetInt32(3)
              Todo = if r.IsDBNull(4) then None else Some(r.GetString(4))
              Priority = if r.IsDBNull(5) then None else Some(r.GetString(5))
              Scheduled = if r.IsDBNull(6) then None else Some(r.GetString(6))
              Deadline = if r.IsDBNull(7) then None else Some(r.GetString(7))
              Title = r.GetString(8)
              Properties = if r.IsDBNull(9) then "" else r.GetString(9)
              Olp = if r.IsDBNull(10) then "" else r.GetString(10) })

    member this.PopulateNode(nodeId: string) : RoamNode option =
        match this.GetNode(nodeId) with
        | None -> None
        | Some node ->
            let tags = this.GetTags(nodeId)
            let aliases = this.GetAliases(nodeId)
            let refs = this.GetRefs(nodeId) |> List.map fst

            // Get file info
            let fileSql = "SELECT title, hash, atime, mtime FROM files WHERE file = @file"

            let fileInfo =
                executeReader fileSql [ "@file", box node.File ] (fun r ->
                    let title = if r.IsDBNull(0) then None else Some(r.GetString(0))
                    let hash = r.GetString(1)
                    let atime = DateTime.Parse(r.GetString(2))
                    let mtime = DateTime.Parse(r.GetString(3))
                    title, hash, atime, mtime)
                |> List.tryHead

            Some
                { Id = node.Id
                  File = node.File
                  FileTitle = fileInfo |> Option.bind (fun (t, _, _, _) -> t)
                  FileHash = fileInfo |> Option.map (fun (_, h, _, _) -> h)
                  FileAtime = fileInfo |> Option.map (fun (_, _, a, _) -> a)
                  FileMtime = fileInfo |> Option.map (fun (_, _, _, m) -> m)
                  Level = node.Level
                  Point = node.Pos
                  Todo = node.Todo
                  Priority = node.Priority
                  Scheduled = node.Scheduled
                  Deadline = node.Deadline
                  Title = node.Title
                  Properties = Domain.parseElispAlist node.Properties
                  Olp = Domain.parseElispList node.Olp
                  Tags = tags
                  Aliases = aliases
                  Refs = refs }

    member _.ClearAll() =
        for table in [ "links"; "citations"; "refs"; "tags"; "aliases"; "nodes"; "files" ] do
            executeNonQuery (sprintf "DELETE FROM %s" table) [] |> ignore

    interface IDisposable with
        member this.Dispose() = this.Close()
