# Reproduce

Requires Linux x64, .NET 8 SDK, `timeout` (GNU coreutils), and NuGet access for the existing xUnit suite. No Godot required.

In this environment:

```bash
bash /workspace/pr2-verification/run.sh
```

Elsewhere unpack the archive, enter `pr2-verification`, ensure `dotnet` is on PATH, and run `bash run.sh`. The script optionally sources the retained cloud tooling environment if present; the .NET SDK itself is not included in this evidence archive. All ProjectReferences are relative. `source` is the pristine PR #2 source snapshot; `hook-source` differs by the one line in `hook.patch`. Neither Git credentials nor Git metadata are included.

Existing baseline suite:

```bash
dotnet test source/tests/AntSim.Core.Tests/AntSim.Core.Tests.csproj -c Debug
```

The console harness checks observations of the current defects. Exit 0 means the documented observations matched, not that the product is defect-free. Each child process has a 20-second watchdog; events and task completion checks have 5-second limits. `run.sh` overwrites per-mode logs on rerun; preserve the original evidence before rerunning if needed.

For target provenance use `logs/remote.txt`, `logs/remote-final.txt`, `logs/target-after.txt`, `logs/source-integrity.json`, and `MANIFEST.sha256`. Target is `da05632d1c0a3cbbe0acd569f805ba7f6bd34376`. To reconstruct a Git-backed checkout independently, clone the repository and checkout this SHA detached; do not use main or PR #3. Recreate hook-source from `git archive` and add only the documented hook line if rebuilding the experiment from scratch.
