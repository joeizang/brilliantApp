# Styling: theme tokens and Tailwind

All styling is Tailwind. Components carry utility classes in their markup; there are no per-component CSS files. Two stylesheets make it work:

| File | Holds |
| --- | --- |
| `src/Brilliant.Lessons.UI/wwwroot/theme.css` | The design tokens as CSS variables (dark default, light under `prefers-color-scheme: light`), the reduced-motion rule and the code-editor token colours (`.tok-*`) |
| `src/Brilliant.Lessons.UI/styles/tailwind.css` → `wwwroot/tailwind.css` | Tailwind's names for those tokens, the keyframes, base element rules, and a few shared component classes |

`index.html` links `theme.css` first, then `tailwind.css`. The built `wwwroot/tailwind.css` is committed.

## Using it

Write utilities in a `.razor` (or `.cs`) file, rebuild the stylesheet, and commit it with the markup:

```razor
<button class="primary btn-primary" @onclick="Check">Check</button>
<li class="@Styling.Option(state)">
```

```bash
scripts/build-styles.sh            # rebuild wwwroot/tailwind.css
scripts/build-styles.sh --check    # fail if the committed file is stale (for CI)
```

Tailwind v4 reads class names straight out of `src/Brilliant.Lessons.UI/**/*.razor` and `*.cs`, and `src/Brilliant.App/**/*.razor` / `index.html`
(the `@source` lines in `styles/tailwind.css`), so a class must be written out **in full** in one of those files. `class="p-@n"` will not
work; choose between whole class names. State-dependent class lists live in `Styling.cs` or a `private const`/method in the component, and
should make the choices exclusive (a `text-left` and a `text-right` in one list would both be emitted, and one would win by source order).

## How it is wired to the design

- **No Preflight.** Only Tailwind's theme and utilities are imported, so it never resets the browser defaults the app relies on. Base
  typography (headings, `p`, `kbd`, `code`, focus ring) is in `@layer base` in `tailwind.css`.
- **Layers.** `theme, base, components, utilities`. The shared component classes sit below utilities, so a utility in the markup
  (`mt-4`, `rounded-sm`) always wins over what a component class sets. `@apply` cannot take a class defined in `@layer components`; use a selector list.
- **Spacing is Tailwind's step** (`0.25rem`): `p-1` is 4px at the default size, `p-2` 8, `p-4` 16, `p-6` 24, `p-12` 48. Everything is in `rem`, so the UI scales as one.
- **Colours are the tokens.** The default palette is removed; `bg-surface`, `bg-surface-raised`, `text-fg`, `text-fg-secondary`, `border-hairline`,
  `text-accent`, `text-danger` … resolve to `var(--surface)`, `var(--fg)`… and so follow light/dark. A raw colour such as `bg-red-500` does not exist, on purpose.
  Mixes with another colour (`bg-success-soft`, `bg-accent-wash`…) are named in `@theme`.
- **Other tokens:** `rounded-sm/md/lg`, `font-ui`/`font-mono`, `ease-app`, `animate-fade-up` etc. are in `@theme`; lengths such as `--content-width` and
  `--touch` are used as `max-w-(--content-width)`, `min-h-(--touch)`.
- **Variants.** `hover:` applies on every pointer (as the app always did). `wide:` is a window of about 900px or more (the sidebar), `narrow:` is 600px or less.
- **Shared component classes** (`tailwind.css`): `content-column`, `lesson-step`, `code-block` (and every `pre`), `option-card`, `panel`, `progress-bar`/`progress-fill`,
  `btn-primary`, `btn-secondary`, `btn-link`, `btn-reset`, `font-inherit`.

## Hook classes

Tests and the browser select on state words that are not styling: `primary`, `secondary`, `link`, `option right|wrong|selected`, `verdict correct|incorrect`,
`row added|updated|removed|looked-up`, `pointer moved`, `trace-player`, `hint-*`, `review-*` and so on. They stay in the class list **next to** the utilities.
Don't remove one without checking the bUnit tests.

## One trap

Tailwind generates a utility for any word in a scanned file that happens to be one: `block`, `table`, `flex`, `static` and `transform` appear in the
output just from prose and comments. They do nothing unless an element carries that class, but it means **an app class must not be named after a
Tailwind utility**. `TraceTable` used `class="table"`, which would have become `display: table`; it is now `trace-table`.

## Build

Mirrors the CodeMirror bundle: sources in `src/Brilliant.Lessons.UI/styles/` (a `package.json`, its lockfile and `tailwind.css`), output committed as
`wwwroot/tailwind.css`, `node_modules` ignored, the folder excluded from the Razor class library's content in the csproj. Node is only needed to
rebuild; building or running the app needs no Node.

## Look and feel: ember on graph paper

A dark midnight-ink default (the light variant follows the OS), a faint dot grid behind everything, and one warm colour, ember, reserved for what is
alive: the streak flame, the daily-goal ring, the due-review badge. The accent blue is for actions.

- **Type.** Bricolage Grotesque for the UI, JetBrains Mono for code, both bundled in `wwwroot/fonts/` (SIL OFL; licences alongside) so the app is the same offline, on
  Android and on the Mac. `--font-ui` and `--font-mono` in `theme.css` name them.
- **Icons** are `Icon.razor`: one outline SVG set (home, tracks, review, sync, check, cross, lock, back, plus, minus, circle, half), drawn in `currentColor`, always
  `aria-hidden`; the control around it carries the accessible name. Size them with `size-*` utilities (the tab bar uses `size-7`). An unknown name throws.
- **Flame** is `Flame.razor`: three layered tongues that lick at different rates, a sway, a glow, and sparks that rise. Lit (a streak is alive), cold (grey, still) or
  roaring (today's goal is met). Its parameters are `Lit`, `Roaring` and `Size`.
- **Motion** answers something the learner did or marks something alive: the flame, the goal ring filling, the current tab's icon popping, the tick drawing and the
  cross shaking after an answer. Everything is switched off by the `prefers-reduced-motion` rule in `theme.css`.
- **Android.** `* { -webkit-tap-highlight-color: transparent }` is in the base layer: the WebView's own flash on tap fought the pressed states.
- **Dimming a card** with `opacity` lets its shadow show through the panel as a ghost. Dim the contents (`[&>*]:opacity-60`) and drop the shadow, as locked lessons do.
