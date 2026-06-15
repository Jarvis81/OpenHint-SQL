# Scripts

These are contributor and maintenance utilities. End users should normally install OpenHint SQL from the GitHub Releases installer.

- `install.bat` builds the extension and installs it into detected SSMS versions. Run as Administrator.
- `Uninstall.bat` removes the manually installed extension from detected SSMS versions. Run as Administrator.
- `build-installer.bat` builds the release installer. Requires Inno Setup 6 and writes `dist\OpenHintSQLSetup-1.1.0.exe`.
- `Check-SqlConnection.ps1` is a local diagnostic helper for SQL connection troubleshooting.
