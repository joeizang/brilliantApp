# Styling: theme tokens, scoped CSS and Tailwind

The app has three layers of CSS. Knowing which wins is the whole trick.

| Layer | Where | Wins over |
| --- | --- | --- |
| Theme tokens and shared rules | `src/Brilliant.Lessons.UI/wwwroot/theme.css` | utilities |
| Component styles | `*.razor.css` next to each component (Blazor scopes them) | utilities |
| Tailwind utilities | `wwwroot/tailwind.css`, built from `styles/tailwind.css` | nothing |

Tailwind's output is in a CSS cascade layer (`@layer utilities`); `theme.css` and the scoped files are unlayered, and unlayered CSS beats
layered CSS whatever the selector. So **a utility only takes effect where no existing rule sets that property**, and adding Tailwind cannot
change how anything already styled looks. To restyle something that has an old rule, edit the old rule.

## Using it

Put utility classes in a `.razor` file:

```razor
<section class="hint-ladder mx-auto box-border max-w-(--content-width) px-6">
```

then rebuild the stylesheet, and commit it with the markup:

```bash
scripts/build-styles.sh            # rebuild wwwroot/tailwind.css
scripts/build-styles.sh --check    # fail if the committed file is stale (for CI)
```

Tailwind v4 reads class names straight out of `src/Brilliant.Lessons.UI/**/*.razor` and `src/Brilliant.App/**/*.razor`/`index.html`
(the `@source` lines in `styles/tailwind.css`), so a class that is not written out in full in one of those files is not generated.
`class="p-@n"` will not work; pick between whole class names instead.

## How it is wired to the design

- **No Preflight.** Only Tailwind's theme and utilities are imported, so it never resets the app's own typography or form styles.
- **Spacing already matches.** Tailwind's step is `0.25rem`, so `p-1` is `--space-xs`, `p-2` is `--space-sm`, `p-4` is `--space-md`,
  `p-6` is `--space-lg` and `p-12` is `--space-xl`. Everything stays in `rem`, so the UI still scales as one.
- **Colours are the tokens.** The default Tailwind palette is removed; `bg-surface`, `bg-surface-raised`, `text-fg`, `text-fg-secondary`,
  `border-hairline`, `text-accent`, `text-danger` and so on resolve to `var(--surface)`, `var(--fg)`… and so follow light/dark. A raw
  colour such as `bg-red-500` does not exist, on purpose.
- **Other tokens** are used with the arbitrary-value syntax: `max-w-(--content-width)`, `rounded-(--radius-md)`.

## Build

Mirrors the CodeMirror bundle: sources in `src/Brilliant.Lessons.UI/styles/` (a `package.json`, its lockfile and `tailwind.css`), output
committed as `wwwroot/tailwind.css`, `node_modules` ignored, the folder excluded from the Razor class library's content in the csproj.
Node is only needed to rebuild; building or running the app needs no Node. `index.html` links `tailwind.css` straight after `theme.css`.

## One trap

Tailwind generates a utility for any word in a scanned file that happens to be one: `block`, `table`, `flex`, `static` and `transform`
appear in the output just from prose and comments. They do nothing unless an element carries that class, but it means **an app class must
not be named after a Tailwind utility**. `TraceTable` used `class="table"`, which would have become `display: table`; it is now `trace-table`.
