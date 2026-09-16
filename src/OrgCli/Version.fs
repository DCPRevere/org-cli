module OrgCli.Version

open System.Reflection

/// Use the executable assembly, even when invoked through a test host or library caller.
let value =
    let assembly = Assembly.GetExecutingAssembly()

    assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    |> Option.ofObj
    |> Option.map (fun attribute -> attribute.InformationalVersion.Split('+').[0])
    |> Option.defaultWith (fun () -> assembly.GetName().Version.ToString())
