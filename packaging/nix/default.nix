{ lib, buildDotnetModule, dotnetCorePackages }:

buildDotnetModule rec {
  pname = "org-cli";
  version = builtins.head (builtins.match ".*<Version>([^<]+)</Version>.*" (builtins.readFile ../../Directory.Build.props));

  src = ../..;

  projectFile = "src/OrgCli/OrgCli.fsproj";
  nugetDeps = ./deps.json;

  dotnet-sdk = dotnetCorePackages.sdk_9_0;
  dotnet-runtime = dotnetCorePackages.runtime_9_0;

  executables = [ "org" ];

  meta = with lib; {
    description = "Standalone Org notes and task management for humans and agents";
    homepage = "https://github.com/dcprevere/org-cli";

    mainProgram = "org";
  };
}
