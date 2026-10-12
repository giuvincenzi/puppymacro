# Git workflow

## Every change in its own worktree

For every change, however small (code, docs, rules, workflows):

1. `git fetch origin`, then create a new worktree from `origin/main` on a new branch:
   `git worktree add .worktrees/<branch> -b <branch> origin/main`
   (`.worktrees/` is ignored by git). Use a short descriptive branch name, e.g. `fix-window-size`.
2. Work, build and test inside that worktree. The `make` commands work from any worktree.
3. Push the branch and open a pull request to `main`, then stop and tell the user.
4. **Claude never merges on its own.** Merge only after the user explicitly approves that pull
   request, with the checks green. Then remove the worktree:
   `git worktree remove .worktrees/<branch>` and delete the local branch (GitHub deletes the
   remote one on merge).

Never commit directly on `main` and never push to `main`.

## `main` is protected

- Changes reach `main` only through a pull request.
- The ruleset "Protect main" (Settings > Rules) requires the checks **`unit_test`** and **`e2e_test`** (the `test`
  workflow's jobs) to pass, with the branch up to date with `main`. When a pull request changes no file that can
  change the tests' result they pass without running them (`testing.md`, "Which changes run the tests on GitHub").
- The release pull request ("Release X.Y.Z", branch `release/X.Y.Z`) is opened only by the `create_release_pr`
  workflow, never by hand; it is the only pull request that may change `PuppyMacro/Version.props`, and merging it
  publishes the release (`publish_release`, `build-and-release.md`).
