# SX3 Scanner Auto Update

```mermaid
sequenceDiagram
    actor User
    participant App as SX3 Scanner
    participant Update as UpdateService
    participant GitHub as GitHub Releases API
    participant Asset as GitHub Release Asset
    participant Setup as SX3ScannerSetup.exe
    participant Server as AnnouncementServer

    App->>Server: Start with parent PID and shared shutdown event
    App->>Update: CheckForUpdateAsync()
    Update->>GitHub: GET /releases/latest
    GitHub-->>Update: tag, notes, installer asset, size, SHA256 digest
    Update->>Update: Validate HTTPS, hostname, version and digest
    Update-->>App: UpdateInfo

    User->>App: Click Update
    App->>Update: DownloadAndVerifyAsync()
    Update->>Asset: GET SX3ScannerSetup-*.exe over HTTPS
    Asset-->>Update: Installer bytes
    Update->>Update: Calculate and compare SHA256

    alt Verification fails
        Update->>Update: Log failure and delete temporary file
        Update-->>App: Reject installer
        App-->>User: Show error
    else Verification succeeds
        Update-->>App: Verified Temp installer path
        App-->>User: Show existing release notes confirmation
        alt User cancels
            App-->>User: Keep application running
        else User confirms
            App->>Setup: Process.Start (interactive)
            App->>Server: Signal graceful shutdown
            App->>App: Application.Shutdown()
            Setup->>Server: Signal graceful shutdown before install
            Setup->>Setup: Install files
            Setup->>Server: Start server
            Setup->>App: Start SX3 Scanner
        end
    end

    User->>App: Close application
    App->>Server: Signal graceful shutdown and wait
```

The updater never replaces the running executable. It downloads to
`%TEMP%\SX3Scanner\Updates\SX3ScannerSetup.exe`, requires the GitHub asset
`digest` to contain SHA256, verifies the file, asks for confirmation, and only
then starts the interactive installer.

`version.json` remains in the repository only as a migration manifest for
legacy clients through v7.2.2. `UpdateService` does not read it.
