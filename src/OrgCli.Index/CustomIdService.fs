module OrgCli.Index.CustomIdService

open OrgCli.Org

/// Generate a CUSTOM_ID that doesn't exist in the database.
/// Tries up to 10 candidates per length, then increments length.
let generateUnique (db: IndexDatabase.OrgIndexDb) : string =
    let count = db.CountCustomIds()
    let mutable length = CustomId.recommendedLength count 0.01
    let mutable result = None

    while result.IsNone do
        let mutable tries = 0

        while tries < 10 && result.IsNone do
            let candidate = CustomId.generate length

            if not (db.CustomIdExists(candidate)) then
                result <- Some candidate

            tries <- tries + 1

        if result.IsNone then
            length <- length + 1

    result.Value
