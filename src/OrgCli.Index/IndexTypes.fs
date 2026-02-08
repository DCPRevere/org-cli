namespace OrgCli.Index

open System

type IndexedFile =
    { Path: string
      Hash: string
      Mtime: int64 }

type IndexedHeadline =
    { File: string
      CharPos: int64
      Level: int
      Title: string
      Todo: string option
      Priority: string option
      Scheduled: string option
      ScheduledDt: string option
      Deadline: string option
      DeadlineDt: string option
      Closed: string option
      ClosedDt: string option
      Properties: string option
      Body: string option
      OutlinePath: string option }

type IndexedTag =
    { File: string
      CharPos: int64
      Tag: string
      Inherited: bool }

type FtsResult =
    { File: string
      CharPos: int64
      Title: string
      OutlinePath: string option
      Context: string option
      Rank: float }

type HeadlineQueryResult =
    { File: string
      CharPos: int64
      Title: string
      Level: int
      Todo: string option
      Priority: string option
      Scheduled: string option
      Deadline: string option
      Closed: string option
      OutlinePath: string option
      Properties: string option }

type AgendaQueryResult =
    { File: string
      CharPos: int64
      Title: string
      Level: int
      Todo: string option
      Priority: string option
      Scheduled: string option
      ScheduledDt: string option
      Deadline: string option
      DeadlineDt: string option
      OutlinePath: string option }
