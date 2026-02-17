module OrgCli.Org.CustomId

open System

let private alphabet = "abcdefghijklmnopqrstuvwxyz0123456789"
let private alphabetLen = alphabet.Length // 36

/// Generate a random base36 string of the given length.
let generate (length: int) : string =
    let rng = Random.Shared
    let chars = Array.init length (fun _ -> alphabet.[rng.Next(alphabetLen)])
    String(chars)

/// Minimum ID length where collision probability stays below threshold
/// for a given existing population. Minimum return value is 3.
let recommendedLength (existingCount: int) (threshold: float) : int =
    let rec find len =
        let slots = pown (float alphabetLen) len
        let ratio = float existingCount / slots

        if ratio < threshold then len else find (len + 1)

    max 3 (find 3)
