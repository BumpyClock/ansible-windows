# Prepared opt-in Windows push check

This hook targets **stack/10-docs**, including its newer linked app/probe sources
in `Ansible.Tests`. Native Windows qualification is still pending. Fixture
success is not evidence that Windows formatting or application tests pass.

Requirements are Windows x64, Git Bash, PowerShell 7 and .NET SDK **10.0.100**
from `global.json`. Prepare the locked deterministic-test graph explicitly:

```powershell
dotnet restore .\Ansible.Tests\Ansible.Tests.csproj --locked-mode
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
.\tools\Install-GitHooks.ps1
```

The hook offline-restores only this test graph from the prepared global NuGet
cache. It checks whitespace through the test project (including linked sources),
then folder whitespace for all app/core/probe C# sources without loading the app
MSBuild graph. The app's `PublishDeviceProbe` target runs `dotnet publish` before
build, so loading that project in a push hook is intentionally avoided. Folder
whitespace does not prove app semantic correctness. It runs the full existing
`Ansible.Tests` command with `--filter 'Category!=NativeIntegration'` and
`--no-restore`. Every external dotnet failure exits with its original code.

No model downloads, recognition, microphone/device access, publishing, signing
or app launches run here. Native/model, UI and MSIX qualification remain separate
Windows gates in `docs/development.md` and `docs/native-validation.md`. Test-graph
lockfiles were generated and re-restored on Linux; Windows validation remains a
gate before adoption. No Windows app lockfile or publishing recipe is changed.

## Push/ref contract

The executable reads all pre-push stdin records before checking anything. Every
non-deletion ref must equal checked-out HEAD. Other commits and annotated tag
objects fail explicitly. Deletion-only pushes need no checks. Empty stdin runs
full checks, not a false green. New branches, unknown remote bases, renames,
deleted files, shared configuration and dependency changes all use full scopes.
This deliberately avoids an affected-file selector for these small projects.

Use a clean checkout, including untracked files. The hook never stashes, fixes,
formats, installs tools, downloads packages or changes tracked files. Generated
compiler/test outputs are expected. Ignored files and installed dependency caches
must belong to this checkout; local hooks do not establish remote CI trust.
Prepare dependencies explicitly after manifest/lock changes, outside the hook.

Installation is opt-in. `scripts/install-hooks.sh` selects repository-local
`core.hooksPath=.githooks`. It is idempotent and refuses any other configured hook
path or existing non-sample default hook. Review rather than overwrite those
hooks. Linked worktrees share this repository-local setting. Nothing installs on
clone or dependency install. To stop using these hooks, first verify the current
value is `.githooks`, then run `git config --local --unset core.hooksPath`.

## Fixture tests

Run `python3 .githooks/tests/test_hooks.py` in a disposable development environment.
Python 3 and Git are required. Windows profiles additionally require PowerShell 7.
Tests create and commit only temporary fixture repos, including a linked worktree.
They invoke the actual executable hook and installer. Stub tool commands verify
full scopes, failures, multi-ref/empty/new/deletion/rename cases, whitespace paths,
clean-HEAD enforcement and existing-hook preservation without outbound pushes.
These are hook harness tests, not application or native-platform qualification.
No local latency target is promised.
