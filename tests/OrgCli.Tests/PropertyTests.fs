module PropertyTests

open FsCheck
open FsCheck.FSharp
open FsCheck.FSharp.GenBuilder
open FsCheck.Xunit
open OrgCli.Org

/// Generator for valid org headline content strings.
let private genHeadline =
    gen {
        let! level = Gen.choose (1, 5)
        let stars = System.String('*', level)
        let! hasTodo = Gen.elements [ true; false ]
        let! hasPriority = Gen.elements [ true; false ]
        let! hasTag = Gen.elements [ true; false ]

        let todoKeywords = [| "TODO"; "DONE"; "NEXT"; "WAITING" |]
        let! todoIdx = Gen.choose (0, todoKeywords.Length - 1)
        let todo = if hasTodo then " " + todoKeywords.[todoIdx] else ""

        let! priChar = Gen.elements [ 'A'; 'B'; 'C' ]
        let priority = if hasPriority then sprintf " [#%c]" priChar else ""

        let! titleWords = Gen.choose (1, 5)

        let! title =
            Gen.arrayOfLength
                titleWords
                (Gen.elements [| "task"; "item"; "note"; "idea"; "plan"; "review"; "fix"; "test" |])
            |> Gen.map (fun ws -> System.String.Join(" ", ws))

        let tags = if hasTag then " :work:urgent:" else ""

        let headline = sprintf "%s%s%s %s%s" stars todo priority title tags

        let! hasProperties = Gen.elements [ true; false ]
        let! hasBody = Gen.elements [ true; false ]
        let id = System.Guid.NewGuid().ToString()

        let props =
            if hasProperties then
                sprintf "\n:PROPERTIES:\n:ID: %s\n:END:" id
            else
                ""

        let! bodyWords = Gen.choose (0, 3)

        let! body =
            Gen.arrayOfLength bodyWords (Gen.elements [| "Some body text."; "Another line."; "Details here." |])
            |> Gen.map (fun ws ->
                if ws.Length = 0 then
                    ""
                else
                    "\n" + System.String.Join("\n", ws))

        return headline + props + body + "\n"
    }

/// Generator for multi-headline documents
let private genDocument =
    gen {
        let! headlineCount = Gen.choose (1, 5)
        let! headlines = Gen.listOfLength headlineCount genHeadline
        return System.String.Join("", headlines)
    }

type OrgDocArb =
    static member OrgDocument() : Arbitrary<string> = genDocument |> Arb.fromGen

// --- Property: parse is total (never throws) on generated headlines ---

[<Property(Arbitrary = [| typeof<OrgDocArb> |], MaxTest = 200)>]
let ``parse never throws on generated org content`` (content: string) =
    let doc = Document.parse content
    doc.Headlines.Length >= 0

// --- Property: parse is deterministic ---

[<Property(Arbitrary = [| typeof<OrgDocArb> |], MaxTest = 200)>]
let ``parse is deterministic — parsing same content twice yields same result`` (content: string) =
    let doc1 = Document.parse content
    let doc2 = Document.parse content

    doc1.Headlines.Length = doc2.Headlines.Length
    && List.forall2
        (fun (h1: Headline) (h2: Headline) ->
            h1.Title = h2.Title
            && h1.Level = h2.Level
            && h1.TodoKeyword = h2.TodoKeyword
            && h1.Priority = h2.Priority
            && h1.Tags = h2.Tags
            && h1.Position = h2.Position)
        doc1.Headlines
        doc2.Headlines

// --- Property: addTag preserves headline count ---

[<Property(MaxTest = 100)>]
let ``addTag preserves headline count`` () =
    let prop =
        Prop.forAll (Arb.fromGen (Gen.elements [| "Task A"; "Task B"; "Note C" |])) (fun title ->
            let content = sprintf "* TODO %s\nBody.\n" title
            let before = Document.parse content
            let after = Document.parse (Mutations.addTag content 0L "newtag")
            before.Headlines.Length = after.Headlines.Length)

    prop

// --- Property: setPriority preserves headline count ---

[<Property(MaxTest = 100)>]
let ``setPriority preserves headline count`` () =
    let prop =
        Prop.forAll (Arb.fromGen (Gen.elements [| "Task A"; "Task B"; "Note C" |])) (fun title ->
            let content = sprintf "* TODO %s\nBody.\n" title
            let before = Document.parse content
            let after = Document.parse (Mutations.setPriority content 0L (Some 'A'))
            before.Headlines.Length = after.Headlines.Length)

    prop

// --- Property: addTag then removeTag is identity for tags ---

[<Property(MaxTest = 100)>]
let ``addTag then removeTag restores original tags`` () =
    let prop =
        Prop.forAll (Arb.fromGen (Gen.elements [| "Task A"; "Task B"; "Note C" |])) (fun title ->
            let content = sprintf "* TODO %s\nBody.\n" title
            let before = Document.parse content
            let added = Mutations.addTag content 0L "tmpXYZ"
            let removed = Mutations.removeTag added 0L "tmpXYZ"
            let after = Document.parse removed
            before.Headlines.[0].Tags = after.Headlines.[0].Tags)

    prop

// --- Property: setTodoState is idempotent ---

[<Property(MaxTest = 100)>]
let ``setTodoState applied twice yields same result as once`` () =
    let now = System.DateTime(2026, 1, 15, 10, 0, 0)

    let config =
        { Types.defaultConfig with
            LogDone = LogAction.NoLog }

    let prop =
        Prop.forAll (Arb.fromGen (Gen.elements [| "Task A"; "Task B" |])) (fun title ->
            let content = sprintf "* %s\nBody.\n" title
            let once = Mutations.setTodoState config content 0L (Some "TODO") now
            let twice = Mutations.setTodoState config once 0L (Some "TODO") now
            let docOnce = Document.parse once
            let docTwice = Document.parse twice

            docOnce.Headlines.[0].TodoKeyword = docTwice.Headlines.[0].TodoKeyword
            && docOnce.Headlines.[0].Title = docTwice.Headlines.[0].Title)

    prop

// --- Property: multi-headline mutation isolation ---

[<Property(MaxTest = 50)>]
let ``mutating first headline does not change second headline title`` () =
    let genPair =
        gen {
            let! t1 = Gen.elements [| "First"; "Alpha"; "Head1" |]
            let! t2 = Gen.elements [| "Second"; "Beta"; "Head2" |]
            return (t1, t2)
        }

    let prop =
        Prop.forAll (Arb.fromGen genPair) (fun (t1, t2) ->
            let content = sprintf "* TODO %s\nBody 1.\n* DONE %s\nBody 2.\n" t1 t2
            let mutated = Mutations.addTag content 0L "newtag"
            let doc = Document.parse mutated
            doc.Headlines.Length = 2 && doc.Headlines.[1].Title = t2)

    prop
