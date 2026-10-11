# Phone and tablet layouts

Issue [#34](https://github.com/joeizang/brilliantApp/issues/34), PRD stories 43, 46, 47 and 48. The same Razor UI runs on a Mac window, an Android tablet and an Android phone; this is how it adapts.

## Screen classes

`ScreenClass` (`Brilliant.Core.Content`) is `Desktop`, `Tablet` or `Phone`. `ScreenClasses.FromWidth` draws the lines:

| Class | Window width | What changes |
|---|---|---|
| Phone | 600px or less | bottom tab bar, phone forms of steps and reviews, touch-sized controls |
| Tablet | up to 895px | bottom tab bar, wider content (the sidebar still doesn't fit) |
| Desktop | 896px and up (`wide`) | sidebar |

Most adaptation is CSS only (below). The rest of the app learns the class from `ScreenInfo`, a singleton fed by `wwwroot/screen.js` (two `matchMedia` queries). `CourseShell` starts watching on first render and stops on dispose. `ScreenInfo.Class` is `Desktop` until the page has answered, and stays `Desktop` if the script can't run, so a failure leaves the desktop forms, which work everywhere. Resizing a window, rotating a phone or splitting the screen raises `Changed`.

## Styling hooks

Tailwind variants in `styles/tailwind.css` (run `scripts/build-styles.sh` after adding classes; `--check` before a PR):

- `wide:` min-width 56rem (the sidebar breakpoint), `narrow:` max-width 600px.
- `coarse:` for `@media (pointer: coarse)`: a finger, not a mouse. Used to grow the Parsons controls to `--touch` (2.75rem). It follows the input device, not the width, so a small Mac window keeps its small buttons.
- Container queries (`@container`, `@3xl:`) where a component cares about its own width rather than the window's, as the trace player does.

In `.razor` files an `@` in a class name has to be written `@@` (`@@container`, `@@3xl:`).

## Navigation: the bottom tab bar (`PhoneNav.razor`)

Below `wide` the sidebar is hidden and a fixed bar at the bottom offers **Today, Tracks, Review (with the due count), Sync**. Flagged steps stay under Tracks. The bar is CSS-visible (`wide:hidden`), so it needs no script. It shows on the places you move between and hides inside a lesson, a session or a review, where the screen has its own Back. The page gets bottom padding so nothing sits under the bar. On Android the web view already ends above the system navigation bar ([android.md](android.md)), so the bar needs no inset of its own.

## Tablet: code beside the trace

The trace player is a container-query grid. When its own box is wide enough (`3xl`, 48rem), the code, controls and status sit on the left (sticky) and the arrays, tables and locals on the right; narrower, they stack as before. Trace lessons are allowed a wider column (`max-w-5xl`).

## Touch Parsons (story 46)

The browser's drag-and-drop doesn't start from a finger in a WebView, so `wwwroot/parsons-touch.js` follows pointer events itself. Touching a row's grip (the `⠿` handle) lifts the row; it follows the finger, the row it would land on is highlighted, the page scrolls near the top and bottom edges, and on release the step is told to move the line (`ParsonsStepView.MoveTo(from, to)`, which calls the same `Board.Move` as the buttons and keyboard). Mouse pointers are left to the native drag. Points worth knowing:

- The drop target is where the finger **lifted**, measured against the rows' resting positions (the lifted row is measured where it started, or it would always be its own target).
- A locked board (after the answer is checked) ignores touch.
- The arrow and indent buttons stay, and grow to touch size, for anyone who would rather tap. On a phone the help text names the handle and the buttons instead of the Mac's ⌥ keys.

## Steps that need a keyboard (stories 43 and 47)

A step can say it is best on a larger screen and offer a tap-friendly version:

```yaml
- id: step.control-flow.write-sum-to
  type: write-code
  title: Sum to n
  prompt: Write `sum_to(n)` …
  largeScreenPreferred: true
  phoneAlternative:
    id: step.control-flow.write-sum-to.arrange   # its own unique id
    type: parsons                                # parsons or fill-blank
    title: Arrange the lines
    prompt: Write `sum_to(n)` … Arrange the lines instead of typing them.
    solution: |                                  # the lines to arrange, as in any parsons step
      def sum_to(n):
          …
    hints: [ … ]
```

Validator rules (`validate content` reports them with file and step):

- `largeScreenPreferred` is for question steps only (choice, predict-output, write-code, fill-blank, parsons).
- `phoneAlternative` needs `largeScreenPreferred: true`, must be `parsons` or `fill-blank`, can't have an alternative of its own and can't itself be `largeScreenPreferred`.
- The alternative has its own id, unique in the pack.

Both fields are additive, so the pack format version stays 1 and older apps ignore them.

### In a lesson

On a phone, a flagged step shows a note: **"This step is best on a larger screen, where you can type the code"**, with a button to **try the arrange-the-lines (or fill-in-the-blank) version** when one exists. Choosing it swaps the step; **Switch back** returns. A step without an alternative keeps the note and stays as written. On a tablet or desktop nothing changes.

Everything the learner does is recorded against the **primary step's id**, whichever form they used: the answer, hints (the alternative's own ladder is shown), flags and completing the step. So progress, mastery and the review schedule don't care which form was used, and a lesson finished on a phone is finished on the Mac. Resizing to a desktop width while the alternative is open puts the original back.

### In Review

On a phone, a re-solve of a flagged step is **presented** as its alternative when it has one, else as the original with a note ("This question is best on a larger screen … You can still answer it here"). `ReviewQueueBuilder.Build(…, screen)` and `LearnerStateProjector.Project(…, screen)` take the class; each `ReviewItemState` carries `Presented` (what to show), `Shown`, `For(screen)` and `NeedsLargeScreen`.

Only the presented question changes. The item id, its schedule and the `ReviewAnswered` event are the same, so the log and the projection don't depend on which device answered ([sync.md](sync.md)). The rating is inferred from the form actually presented. Items after the current one are re-formed if the window changes mid-review.

## Content

All 41 `write-code` steps in tracks 1 and 2 are flagged `largeScreenPreferred`. 18 of them have a Parsons alternative; the rest (for instance those whose solutions are too long to arrange sensibly) show the note only. A validator test keeps new write-code steps from shipping unflagged.

## Where the code is

| Path | Purpose |
|---|---|
| `src/Brilliant.Core/Content/ScreenClass.cs` | `ScreenClass`, `ScreenClasses.FromWidth` |
| `src/Brilliant.Core/Content/ContentModel.cs` | `Step.LargeScreenPreferred`, `PhoneAlternative`, `For(screen)` |
| `src/Brilliant.Core/Review/ReviewQueueBuilder.cs`, `Progress/` | screen-aware queue and projection |
| `src/Brilliant.Lessons.UI/ScreenInfo.cs`, `wwwroot/screen.js` | screen class detection |
| `src/Brilliant.Lessons.UI/PhoneNav.razor` | bottom tab bar |
| `src/Brilliant.Lessons.UI/ParsonsStepView.razor`, `wwwroot/parsons-touch.js` | touch dragging |
| `src/Brilliant.Lessons.UI/LessonViewer.razor`, `ReviewView.razor` | notes and alternatives |
| `src/Brilliant.Lessons.UI/TracePlayer.razor` | tablet split |
| `tools/Brilliant.Cli/ContentSource.cs` | validation of the two fields |
| `tests/js/screen.test.mjs`, `tests/js/parsons-touch.test.mjs` | script logic (`node --test tests/js/*.test.mjs`) |
| `tests/Brilliant.Lessons.UI.Tests/PhoneLayoutTests.cs`, `tests/Brilliant.Core.Tests/PhoneReviewTests.cs` | behaviour |

## Verified, and what is yours to verify

Verified on an Android 16 emulator (Pixel 9 Pro XL image, signed Release APK): the tab bar above the system navigation bar and its badge, the phone Review form (Parsons with handles, big buttons), and a touch drag of a Parsons line from first to third place. Found and fixed there: raw Razor in a tab label, an unpadded Review header, and a drag that always dropped back on its start row.

Not checked on a device: the lesson note and "try the arrange-the-lines version" button (covered by component tests), the tablet trace split, rotation, and the soft keyboard with a fixed tab bar. Please try the touch drag on the Galaxy S24+ in particular, with a real finger.
