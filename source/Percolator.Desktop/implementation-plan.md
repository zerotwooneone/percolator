### Chunk 2: Pre-Unlock Vault Gate (`StartupWindow` Screen 1A & 1B + Duress Decoy)

**Goal**: Deliver the pre-database security gate in a dedicated $480 \times 600\text{ px}$ startup frame, supporting first-time setup, returning user unlock, blank-password confirmation dialogs, failure recovery modals, and instant decoy duress vault initialization.

#### Tasks
- [ ] Implement `Features/Startup/Views/StartupWindow.axaml` & `StartupHostViewModel.cs`:
  - Fixed-size, non-resizable centered frame ($480 \times 600\text{ px}$) hosting Screen 1 and Screen 2 views via dynamic view locator transition.
- [ ] Implement `Features/Startup/Views/VaultSetupView.axaml` & `VaultSetupViewModel.cs` (State 1A):
  - Header: Title *"Welcome to Percolator"* (`Theme="{StaticResource TitleTextBlockStyle}"`), Subtitle *"Choose a master password to protect your messages, keys, and contacts on this device."* (`Theme="{StaticResource CaptionTextBlockStyle}"`, `Foreground="{DynamicResource TextFillColorSecondaryBrush}"`).
  - Master Password + Confirm Password fields (`PasswordChar="•"` with built-in reveal toggle).
  - Expandable `[+ Add Emergency Duress Passphrase]` progressive disclosure link.
  - Validation: Duress cannot match master password; cannot be whitespace-only.
  - Blank Password Dialog: If password is empty, displays `ContentDialog`: *"Skip password protection? Your vault will still be encrypted with a random key, but that key will be accessible without a password."* with actions `[ Cancel ]` and `[ Continue Without Password ]`.
  - Calls `IDatabaseUnlockCoordinator.InitializeAsync(masterPassword, duressPassword, ct)`.
- [ ] Implement `Features/Startup/Views/VaultUnlockView.axaml` & `VaultUnlockViewModel.cs` (State 1B):
  - Header: *"Percolator"* / *"Unlock Vault"* (`Theme="{StaticResource TitleTextBlockStyle}"`).
  - Auto-focused master password field with show/hide toggle.
  - Inline loading spinner during Argon2id key derivation.
  - Error feedback: subtle shake animation and critical inline text (*"Incorrect password. Please try again."*) on native error `26` (`SQLITE_NOTADB`).
  - Auto-launch preference bypass: If user enabled auto-start for a persona, unlock immediately launches `MainWindow`. Holding `Shift` (or `Shift+Enter`) overrides bypass and forces Screen 2 to appear.
  - Emergency Duress Activation: Entering duress passphrase cryptographically shreds real header in $< 2\text{ ms}$, creates a valid decoy SQLite DB encrypted with duress key, and transitions smoothly into Screen 2 (State 2B: Empty Vault) with zero error popups or forensic crash signatures.
- [ ] Implement Startup Failure Recovery Dialogs:
  - Corrupted Vault Modal: If database header is unparseable or damaged, displays *"Vault Data Damaged"* dialog (*"The database file could not be opened. It may have been damaged or altered."*) with actions `[ Open Data Folder ]` and `[ Exit ]`.
  - Security Engine Error Modal: If native cryptographic smoke test fails (`SignalCrypto.TestConnection() != 42` due to missing OS VC++ runtimes), displays *"Security Engine Error"* dialog (*"The native cryptographic library (signal_shim) could not be loaded on your system."*) with actions `[ View Diagnostic Log ]` and `[ Exit ]`.

#### Verification
- Fresh install displays `VaultSetupView` with proper typography hierarchy.
- Initializing with empty password prompts confirmation dialog.
- Subsequent launch displays `VaultUnlockView`.
- Incorrect password displays inline error without crashing.
- Corrupted file or missing native DLL triggers respective recovery dialogs with data folder / log access.
- Duress passphrase wipes vault and smoothly presents empty vault state.

---

### Chunk 3: Multi-Persona Gate & Stealth Port Binding (Screen 2)

**Goal**: Deliver Screen 2 within the startup window frame, enforcing total network stealth until persona confirmation, displaying per-persona network port status pills, auto-resolving port conflicts, and supporting persona CRUD.

#### Tasks
- [ ] Implement `Common/Storage/DesktopIdentitySettingsEntity.cs` & `IDesktopSettingsRepository`:
  - Local desktop database entity (PK `IdentityId`, `PreferredPort`, `LastBoundPort`, `AutoStart`, `SidebarOrder`).
  - Stores UI preferences and network bindings without polluting domain `IdentityProfile`.
- [ ] Implement `Common/Services/PortReservationService.cs` (`IPortReservationService`):
  - Probes local TCP/UDP port availability in default range `5100`–`5299`.
  - Auto-assigns next free port on conflict.
- [ ] Implement `Features/Startup/Views/IdentityGateView.axaml` & `IdentityGateViewModel.cs`:
  - **State 2A (Normal Return)**:
    - Multi-select persona cards (`Border.persona-card`).
    - Port status pill: Green dot (`Port 5100 Ready`), Orange dot (`Port 5100 in use → Auto-selected 5101`), Red alert pill (`All default ports busy`).
    - Inline port editor popover (`[ ✎ ]`) to customize port.
    - Card Context Menu (right-click or `···` kebab menu): `Edit Profile` (display name/avatar) and `Delete Persona`.
    - Port Race Condition Recovery: If a port is claimed by another OS process between selection and launch, displays inline banner (*"Port 5101 was claimed by another application. Re-probing next available port..."*) and automatically re-leases the next free port.
    - Single-persona auto-start checkbox: `[ ] Always auto-start with this persona on unlock`.
    - `[ Launch Selected Personas → ]` primary button (disabled if zero checked or conflict unresolved).
  - **State 2B (Empty Vault)**:
    - Centered card: *"No personas yet"*, with friendly tip callout: *"Tip: Many people create separate personas for Work vs. Personal, or Family vs. Friends."*
    - `[ Create Your First Persona ]` action button.
- [ ] Implement `CreatePersonaDialog.axaml` & `CreatePersonaDialogViewModel.cs`:
  - Display name (max 32 chars, whitespace-trimmed), avatar picker with identicon generator, and advanced port settings.
- [ ] Implement `DeletePersonaDialog.axaml` & `DeletePersonaDialogViewModel.cs`:
  - Selectable persona name box so power users can copy with keyboard/mouse.
  - Confirmation prompt requiring user to type exact persona name to enable `[ Shred & Delete Persona ]` (`Button.danger`).

#### Verification
- Launching on empty/decoy vault shows State 2B with tip callout.
- Creating persona persists keypair in `ILocalIdentityKeyStore`, profile in `IIdentityProfileRepository`, and settings in `DesktopIdentitySettingsEntity`.
- Simulating port conflict updates pill to orange with auto-selected next port.
- Simulating race condition during launch triggers automatic re-probing banner and re-leases without crash.
- Deleting persona shreds keys and removes profile only when typed name matches exactly.
