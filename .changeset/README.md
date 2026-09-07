# Changesets

From the repository root, run `npm ci`, then `npm run changeset`. Select
`mhw-dps-meter`, `mhw-log-viewer`, or both; choose a patch, minor, or major bump
and describe the user-visible change. Commit the generated Markdown file with
your code. For maintenance-only changes, no changeset is necessary.

The private npm workspace packages only track versions; nothing is published
to npm. The plugin and viewer retain independent versions. See
[the release guide](../docs/releases.md) for the full process.
