# Optional Linux user service

These commands are available in development after 2.0.0. The released 2.0.0 binary does not yet include them.

Install org-cli normally, then opt into a persistent local server:

```sh
org service install --directory ~/org --mcp
```

The workspace must already exist. Installation saves the workspace, port, MCP and read-only settings, enables the systemd **user** service, restarts it and checks readiness. It prints the board URL (normally `http://127.0.0.1:8765/`) and, when enabled, the MCP endpoint. No sudo is required. Omitting `--mcp` starts only the board/API. Ordinary CLI commands and stdio MCP do not need this service.

```sh
org service install --directory ~/org --port 9000 --mcp --read-only
org service status
org service restart
org service stop
org service uninstall
```

Run install again with all desired options to change the configuration; omitted options return to their defaults (8765, MCP off, read-only off). It manages one workspace/service per user. Stop leaves login startup enabled; uninstall stops and disables it and removes only the setup command's configuration and unit/drop-in. It preserves Org files, indexes, the package-owned unit, and optional credential files.

## Where configuration lives

Files are under `$XDG_CONFIG_HOME` (default `~/.config`), outside the Org workspace. This must match your systemd user manager’s configuration search path; setup detects and reports a shell/session mismatch before writing:

- `org-cli/service.json`: workspace, port and server options.
- `systemd/user/org-cli.service.d/50-org-cli.conf`: generated executable/config paths.
- `systemd/user/org-cli.service`: generated only when the matching packaged unit is unavailable (for example a standalone download).

The AUR package supplies `/usr/lib/systemd/user/org-cli.service`. Merely installing the package does not enable or start it. Linux release archives also include this unit. Standalone binaries embed the same template, so no separate unit download is needed.

Existing custom units or conflicting files are preserved and reported instead of overwritten. The generated files are managed by org-cli; use a separate systemd drop-in for additional customisation. Package upgrades update the vendor unit; restart the server after upgrading. If the executable moves, rerun installation from its new location. Source-build and .NET tool users should rerun setup if their executable or assembly location changes.

## Authentication and other environment settings

Shell environment variables are not automatically copied into the user service. To require a bearer token, create `~/.config/org-cli/service.env` (or the equivalent XDG path), readable only by your account:

```sh
install -d -m 700 ~/.config/org-cli
(umask 077; touch ~/.config/org-cli/service.env)
chmod 600 ~/.config/org-cli/service.env
# Edit this file to add: ORG_API_TOKEN=your-secret-token
org service restart
```

Enter that token in the board or configure it in the API/MCP client. Do not put secrets in service command arguments or commit them to a repository. Additional supported org-cli environment settings can go in this file. The server continues to bind only to localhost. No public hosting or OAuth is enabled by service installation.

## Login versus boot

By default the service starts with your user manager, normally at login. To request operation from boot and after logout:

```sh
loginctl enable-linger "$USER"
```

This affects your user manager and all its enabled services, not just org-cli. Setup and uninstall deliberately do not change lingering. A home directory that is unavailable until login still cannot be used before login.

## Standard systemd tools remain available

```sh
systemctl --user status org-cli
journalctl --user -u org-cli -f
systemctl --user edit org-cli
systemctl --user restart org-cli
systemctl --user disable --now org-cli
```

Setup requires a working systemd user manager. In a container, non-systemd distribution, or session without its user bus, run `org serve -d ~/org --mcp` in the foreground instead. macOS and Windows service managers are not supported by these commands.

If setup fails after writing configuration, it reports the failure and retains the files for diagnosis. Inspect the journal (for example a port may already be occupied), fix the cause, and rerun install or restart. Keep reproducible non-secret settings in your configuration-management system if you use one.
