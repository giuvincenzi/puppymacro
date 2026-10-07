# Git workflow

## Every change in its own worktree

For every change, however small (code, docs, rules, workflows):

1. `git fetch origin`, then create a new worktree from `origin/main` on a new branch:
   `git worktree add .worktrees/<branch> -b <branch> origin/main`
   (`.worktrees/` is ignored by git). Use a short descriptive branch name, e.g. `fix-window-size`.
2. Work, build and test inside that worktree. The `make` commands work from any worktree.
3. Push the branch and open a pull request to `main`.
4. Merge when the checks are green, then remove the worktree:
   `git worktree remove .worktrees/<branch>` and delete the branch.

Never commit directly on `main` and never push to `main`.

## `main` is protected

- Changes reach `main` only through a pull request.
- The pull request needs the checks **Test** (unit tests) and **E2E** (end-to-end tests) to pass.
- Release and Site workflows run only on `main`: on another branch they do nothing.
