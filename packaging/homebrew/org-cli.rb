class OrgCli < Formula
  desc "CLI for org-mode file manipulation and org-roam database management"
  homepage "https://github.com/dcprevere/org-cli"
  version "@@VERSION@@"


  on_macos do
    if Hardware::CPU.arm?
      url "https://github.com/dcprevere/org-cli/releases/download/v#{version}/org-osx-arm64.tar.gz"
      sha256 "@@SHA256_OSX_ARM64@@"
    else
      url "https://github.com/dcprevere/org-cli/releases/download/v#{version}/org-osx-x64.tar.gz"
      sha256 "@@SHA256_OSX_X64@@"
    end
  end

  on_linux do
    if Hardware::CPU.arm?
      url "https://github.com/dcprevere/org-cli/releases/download/v#{version}/org-linux-arm64.tar.gz"
      sha256 "@@SHA256_LINUX_ARM64@@"
    else
      url "https://github.com/dcprevere/org-cli/releases/download/v#{version}/org-linux-x64.tar.gz"
      sha256 "@@SHA256_LINUX_X64@@"
    end
  end

  def install
    bin.install "org"
  end

  test do
    assert_match version.to_s, shell_output("#{bin}/org --version")
  end
end
