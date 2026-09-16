module OrgCli.RoamProperties

open System
open OrgCli.Org

/// Get ROAM_ALIASES as a list
let getRoamAliases (props: PropertyDrawer option) =
    Types.tryGetProperty "ROAM_ALIASES" props
    |> Option.map Types.splitQuotedString
    |> Option.defaultValue []

/// Get ROAM_REFS as a list
let getRoamRefs (props: PropertyDrawer option) =
    Types.tryGetProperty "ROAM_REFS" props
    |> Option.map Types.splitQuotedString
    |> Option.defaultValue []

/// Check if node should be excluded from org-roam
let isRoamExcluded (props: PropertyDrawer option) =
    Types.tryGetProperty "ROAM_EXCLUDE" props
    |> Option.map (fun v -> not (String.IsNullOrWhiteSpace(v)))
    |> Option.defaultValue false
