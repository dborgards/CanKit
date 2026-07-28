# Contributing to CanKit

Thanks for helping! This project aims to provide a single, clean C# API for CAN 2.0 and CAN-FD across multiple vendors.

## Quick start (build & run)

- Prereqs: .NET SDK 8.x
- Build: `dotnet build`
- List available endpoints:  
  `dotnet run --project samples/CanKit.Sample.ListEndpoints`
- Sniff frames on an endpoint:  
  `dotnet run --project samples/CanKit.Sample.Sniffer -- --endpoint <your-endpoint> --bitrate 500000`
- Loopback demo (no hardware):  
  `dotnet run --project samples/CanKit.Sample.QuickStartTxRx -- --src virtual://alpha/0 --dst virtual://alpha/1 --count 5`

> The samples accept flags like `--scheme`, `--fd`, `--brs`, `--bitrate`, etc. See their `Program.cs` for full usage.

## Branching strategy

CanKit.Pro is a long-lived fork of [pkuyo/CanKit](https://github.com/pkuyo/CanKit). We use three permanent branches:

| Branch | Purpose |
| --- | --- |
| `main` | Release branch (GitHub default). Only tested release states land here; version tags (`vX.Y.Z`) are created on this branch, and hotfixes branch off it. |
| `develop` | Integration branch. Feature branches and upstream merges land here; releases are prepared here. |
| `upstream-master` | Read-only mirror of pkuyo/CanKit `master`. **Never commit fork-specific changes to this branch** — it must stay fast-forwardable from upstream. |

### Feature flow

Branch off `develop` and open your PR **against `develop`**. Note that the default PR base is `main`, so change the base manually when opening the PR.

```bash
git checkout -b feat/my-feature develop
```

### Upstream sync

Pull changes from the original CanKit into the fork via `upstream-master`, then merge into `develop` (resolve conflicts there, never on `upstream-master`):

```bash
git remote add upstream https://github.com/pkuyo/CanKit.git   # once
git fetch upstream master
git push origin upstream/master:upstream-master               # fast-forward the mirror
git checkout develop
git merge upstream-master
```

### Release flow

Open a PR from `develop` into `main`. After merging, tag the release on `main`:

```bash
git tag vX.Y.Z main && git push origin vX.Y.Z
```

### Hotfix flow

Branch off `main`, then merge the fix back into **both** `main` and `develop`:

```bash
git checkout -b hotfix/my-fix main
```

## Filing issues

- Use the **Bug report** or **Compatibility test report** templates.
- For questions, please use **Discussions**.

## Coding

- Follow C# conventions. Keep public API minimal and well-documented.
- Prefer small, focused PRs.
- Use **Conventional Commits** in PR titles (e.g., `feat(core): add periodic TX API`).

# Tests

* **Run unit/integration tests:** `dotnet test`.
  To run tests for a modified adapter, set `CANKIT_TEST_ADAPTERS` to the adapter’s project name.


* **Adapter-specific dependencies:** Some adapter tests may require the vendor SDK and/or real hardware to be installed and connected.

* **Configurations:** You can run adapter tests with a **fake configuration** (no hardware). When conditions allow, prefer running in **Release** configuration against real hardware for end-to-end validation:

  ```bash
  dotnet test -c Release
  ```


## Areas / labels

- `area: core`, `area: pcan`, `area: kvaser`, `area: socketcan`, `area: zlg`
