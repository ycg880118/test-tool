# Okta User Manager

A local Blazor Server app to bulk create/update Okta users from a CSV file, with
an editable CSV-column → Okta-profile-field mapping and validation feedback.

## Requirements
- .NET 8 SDK (already installed via `winget install Microsoft.DotNet.SDK.8`)

## Run
```powershell
cd d:\Work\okta-user-manager
dotnet run
```
Then open the URL it prints (e.g. http://localhost:5294) in your browser.

## How it works (4 steps in the UI)
1. **Connection** – enter your Okta org URL (`https://your-org.okta.com`) and an
   SSWS API token (Okta admin → Security → API → Tokens). "Test connection"
   calls `GET /api/v1/users?limit=1`.
2. **Upload CSV** – the first row is treated as headers; it shows the row/column count.
3. **Mapping** – each CSV column gets a dropdown of Okta profile fields.
   - **Auto-map** guesses by matching names (ignoring case/spaces/underscores).
   - **Unmapped columns** are listed (they'll be ignored).
   - **Missing required fields** (login, email, firstName, lastName) block the run.
   - **Save mapping** persists to `mapping.json` so it's reused next time.
4. **Run** – per row, looks the user up by `login` (else `email`):
   found → partial update (`POST /api/v1/users/{id}`); not found →
   create (`POST /api/v1/users?activate=…`). Results stream in with a per-row status.

## Where to customize
- **Okta fields list:** `Models/OktaProfileField.cs` — add your org's custom
  profile attributes here so they appear in the mapping dropdown.
- **Required fields:** mark `Required: true` in that same list.
- **API behavior:** `Services/OktaService.cs` (lookup key, create vs update, payload).
- **CSV parsing options:** `Services/CsvService.cs`.

## Notes
- Blank cell values are not sent to Okta (so a partial update won't wipe a field).
- The API token is kept in memory only; it is not written to disk.
