module OrgCli.Index.IndexQuery

let searchFts (db: IndexDatabase.OrgIndexDb) (query: string) : FtsResult list = db.SearchFts(query)

let queryHeadlines
    (db: IndexDatabase.OrgIndexDb)
    (todo: string option)
    (tag: string option)
    (outlinePathPrefix: string option)
    : HeadlineQueryResult list =
    db.QueryHeadlines(?todo = todo, ?tag = tag, ?outlinePathPrefix = outlinePathPrefix)

let queryAgendaNonRepeating
    (db: IndexDatabase.OrgIndexDb)
    (startDate: string)
    (endDate: string)
    : AgendaQueryResult list =
    db.QueryAgendaNonRepeating(startDate, endDate)

let queryAgendaRepeating (db: IndexDatabase.OrgIndexDb) : AgendaQueryResult list = db.QueryAgendaRepeating()
