module OrgCli.Org.Subtree

open System.Text.RegularExpressions

let private headlineRegex = Regex(@"^(\*+) ", RegexOptions.Multiline)

/// Find the range [start, end) of the subtree rooted at the headline at `pos`.
/// Returns (pos, pos) if pos does not point at a headline.
let getSubtreeRange (content: string) (pos: int64) : int * int =
    let startIdx = int pos
    let m = headlineRegex.Match(content, startIdx)

    if not m.Success || m.Index <> startIdx then
        startIdx, startIdx
    else
        let level = m.Groups.[1].Value.Length

        let endIdx =
            headlineRegex.Matches(content, startIdx + m.Length)
            |> Seq.cast<Match>
            |> Seq.tryFind (fun nm -> nm.Groups.[1].Value.Length <= level)
            |> Option.map (fun nm -> nm.Index)
            |> Option.defaultValue content.Length

        startIdx, endIdx

/// Extract the subtree at `pos` as a string (trimmed of trailing whitespace).
/// Returns "" if pos does not point at a headline.
let extractSubtree (content: string) (pos: int64) : string =
    let (s, e) = getSubtreeRange content pos
    content.Substring(s, e - s).TrimEnd()

/// Remove the subtree at `pos` from content.
/// Returns content unchanged if pos does not point at a headline.
let removeSubtree (content: string) (pos: int64) : string =
    let (s, e) = getSubtreeRange content pos
    content.Remove(s, e - s)

/// Get the body text of the headline at `pos`, excluding child headlines.
/// Returns "" if pos does not point at a headline or the headline has no body.
let getHeadlineBody (content: string) (pos: int64) : string =
    let startIdx = int pos
    let m = headlineRegex.Match(content, startIdx)

    if not m.Success || m.Index <> startIdx then
        ""
    else
        let lineEnd =
            match content.IndexOf('\n', startIdx) with
            | -1 -> content.Length
            | i -> i + 1

        let nextHeadline = headlineRegex.Match(content, lineEnd)

        let bodyEnd =
            if nextHeadline.Success then
                nextHeadline.Index
            else
                snd (getSubtreeRange content pos)

        content.Substring(lineEnd, bodyEnd - lineEnd).TrimEnd()

/// Adjust all headline levels in subtreeContent by delta.
/// Levels are clamped to a minimum of 1. Delta 0 is a no-op.
let adjustLevels (subtreeContent: string) (delta: int) : string =
    if delta = 0 then
        subtreeContent
    else
        headlineRegex.Replace(
            subtreeContent,
            fun m ->
                let currentLevel = m.Groups.[1].Value.Length
                let newLevel = max 1 (currentLevel + delta)
                (String.replicate newLevel "*") + " "
        )

/// Append subtreeContent at the end of the file at level 1.
let appendSubtree (content: string) (subtreeContent: string) : string =
    let subtreeMatch = headlineRegex.Match(subtreeContent)

    if not subtreeMatch.Success then
        content
    else
        let subtreeRootLevel = subtreeMatch.Groups.[1].Value.Length
        let delta = 1 - subtreeRootLevel
        let adjusted = adjustLevels subtreeContent delta
        let needsNewline = content.Length > 0 && content.[content.Length - 1] <> '\n'
        let prefix = if needsNewline then "\n" else ""
        content + prefix + adjusted

/// Insert subtreeContent as a child of the headline at parentPos.
/// The subtree levels are adjusted to be one deeper than the parent.
let insertSubtreeAsChild (content: string) (parentPos: int64) (subtreeContent: string) : string =
    let parentStart = int parentPos
    let parentMatch = headlineRegex.Match(content, parentStart)

    if not parentMatch.Success || parentMatch.Index <> parentStart then
        content
    else
        let parentLevel = parentMatch.Groups.[1].Value.Length
        let subtreeMatch = headlineRegex.Match(subtreeContent)

        if not subtreeMatch.Success then
            content
        else
            let subtreeRootLevel = subtreeMatch.Groups.[1].Value.Length
            let delta = (parentLevel + 1) - subtreeRootLevel
            let adjusted = adjustLevels subtreeContent delta
            let (_, parentEnd) = getSubtreeRange content parentPos
            let needsNewline = parentEnd > 0 && content.[parentEnd - 1] <> '\n'
            let prefix = if needsNewline then "\n" else ""
            content.Insert(parentEnd, prefix + adjusted)
