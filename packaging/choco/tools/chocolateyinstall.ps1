$ErrorActionPreference = 'Stop'
$toolsDir = "$(Split-Path -Parent $MyInvocation.MyCommand.Definition)"
$url = 'https://github.com/dcprevere/org-cli/releases/download/v@@VERSION@@/org-win-x64.zip'

$packageArgs = @{
  packageName   = 'org-cli'
  unzipLocation = $toolsDir
  url64bit      = $url
  checksum64    = '@@SHA256_WIN_X64@@'
  checksumType64= 'sha256'
}

Install-ChocolateyZipPackage @packageArgs
