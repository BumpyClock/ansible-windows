# UX review rubric

This rubric scores one round of screenshots from `Capture-AppScreens.ps1`. It is fixed between rounds. The score is
computed from a checklist, so two reviewers who see the same frames should reach nearly the same number. Do not
give a holistic score, and do not adjust the computed result.

You are an independent senior product designer. Be strict and specific. Score what is on screen, not intent.

## Reviewer rules

- You are given one round folder under `.runtime\ux`. Open every PNG in it and look at each image. Do not score from
  file names.
- Score only what the frames show. Do not read source code, git history, other round folders, or earlier reviews.
- You know nothing about earlier versions. Do not guess at intent or at what changed.
- Do not edit any file. Return the review as plain text.

## Product

Ansible is a local dictation and meeting transcription app for Windows. The user holds a global shortcut, speaks,
and text is inserted at the cursor. The main window has Home (history plus usage insights), Meetings, Speech
models, Dictionary, and Settings. It is built with WinUI 3.

## Owner constraints

These are decisions, not defects. Never count them as instances.

- Stock Windows materials and neutral colours. The title bar and sidebar sit on the Mica base layer. The page sits
  on one more solid content layer (WinUI's layer fill). No custom surface or text colours.
- The citron accent, with ink text on it, is the single brand colour.
- Data is never fabricated. Empty states and small numbers are real. Do not ask for sample content.
- The Windows font (Segoe UI Variable) stays.
- The radial "Daily rhythm" graph on Home stays as drawn (dotted ring, hour ticks and labels, centre total),
  including in periods with no dictations. Its legibility can be scored. Its existence and form cannot.
- The "Microphone access required" notice is an artefact of the test machine. Where it sits and how it looks can be
  scored. That it is present cannot.
- Meeting transcript text and meeting titles are real user data. Do not quote them.
- Frames are captured at 125% or 150% display scale. Slight softness is a capture artefact.

## Screenshot set

Files are named `<page>-<theme>-<size>-<stop>.png`. `wide` is a 1440x960 window. `narrow` is 860x760. `<stop>`
counts scroll positions from the top of the page, about one viewport apart. Settings shows one section at a time,
and its stops run through the sections in tab order. `welcome-*` is the modal shortcut lesson. `meeting-window-*`
is the separate transcript window for a saved meeting.

Read every PNG in the round folder before scoring. Do not sample.

## How to score

There are 7 dimensions with 5 criteria each. For every criterion, list its instances, weigh them in units, and
read the score from the table.

- A minor instance is 1 unit. A careful reviewer notices it. A typical user does not.
- An instance a typical user notices at a glance is 2 units.

| Units | Score |
| --- | --- |
| 0 | 1.0 |
| 1 | 0.75 |
| 2 or 3 | 0.5 |
| 4 or more | 0.25 |
| The problem is on most pages, or it stops the user from doing the page's task | 0 |

Counting rules:

- An instance is one defect on one page or one shared component. Count it once, however many themes, sizes, or
  scroll stops show it. A defect in a shared component (title bar, notice, empty state) is one instance.
- One defect counts under one criterion only: the first criterion in the list that describes it.
- Count only what a frame shows. Do not infer behaviour that no frame shows. If a criterion cannot be judged from
  the frames (for example focus states when no frame shows focus), score it 1.0 and write "not shown".
- If you are unsure between two scores, give the lower one.
- Every score below 1.0 must list its instances with file names.

Arithmetic:

- Dimension score = 10 x the mean of its five criteria, to one decimal.
- Overall score = the mean of the seven dimension scores, to one decimal.

## Criteria

### 1. Hierarchy and layout

- H1 Primary action. On each page the main task or action is visible in the first viewport and is the most
  prominent control.
- H2 Grouping. Related content is grouped and labelled. No loose control or text sits outside a group.
- H3 Use of space. No large empty area sits beside or above dense content in a first viewport, and no column is
  wider than a comfortable reading measure.
- H4 Nothing covers content. No floating or pinned element hides text or controls of the page.
- H5 Page order. Title, subtitle, actions, and content follow the same order and position on every page.

### 2. Consistency

- C1 Type roles. The same role (page title, group heading, row title, description, caption) uses the same size and
  weight on every page.
- C2 Spacing. Page padding, card padding, row height, and gaps between groups are the same on every page.
- C3 Components. The same job uses the same component everywhere (cards, rows, badges, empty states, menus,
  keycaps).
- C4 Controls. Buttons, fields, and toggles in the same role have the same height, width, and style, and sit on
  the same edge.
- C5 Icons and corners. One icon family and size per role. Corner radii match between like elements.

### 3. Windows 11 platform fit

- P1 Materials. The base layer and content layer follow the two-layer model with no off-system surface.
- P2 Stock controls. Controls look and behave like stock WinUI controls in every state shown.
- P3 Title bar and navigation. Both follow Windows 11 conventions for size, placement, and content.
- P4 Patterns. Dialogs, menus, flyouts, tabs, and notices follow Fluent patterns for their job.
- P5 Text conventions. Sentence case, no all-caps labels, and an ellipsis only on commands that need more input.

### 4. Content and states

- S1 Copy. Labels and descriptions are plain, short, and unambiguous. No jargon and no ambiguous command label.
- S2 Empty states. Each one says what is missing and what to do, once, with no duplicate hint in the same view.
- S3 Status and errors. Each is named accurately, stated once, and paired with a next action when one exists.
- S4 Controls match state. No enabled control that can do nothing, and no value shown with false precision.
- S5 Density. Help text does not make rows needlessly tall, and no description ends in a one-word last line.

### 5. Responsive behaviour

- R1 No clipping. Nothing is cut off horizontally and no control overflows its container in the narrow frames.
- R2 Reflow order. The narrow layout keeps primary content first and keeps the page actions reachable.
- R3 Control widths. Controls keep usable widths with no crowding or overlap in the narrow frames.
- R4 Scroll states. A pinned header names the content under it, the title bar names the page once its heading has
  scrolled away, and a pinned bar is visually separated from what scrolls under it.
- R5 Balance. Side-by-side cards or columns in the narrow frames do not leave dead blocks or unequal ends.

### 6. Themes and accessibility

- T1 Parity. Each light and dark pair has the same layout, content, and states.
- T2 Text contrast. All text, including secondary and disabled-looking text that is not disabled, reads clearly in
  both themes.
- T3 Graphic contrast. Icons, logos, chart marks, borders, and toggles read clearly in both themes, with nothing
  glaring and nothing invisible.
- T4 Text size. No text is smaller than the caption size used elsewhere in the app.
- T5 State clarity. Selected, disabled, and focused states (where shown) are distinct, and not by colour alone.

### 7. Craft

- K1 Alignment. Left edges, baselines, and columns line up within each page.
- K2 Clean edges. No clipped glyph, overlap, stray border, or doubled divider. Text that scrolls under a pinned
  bar or out of the viewport edge is normal and is not an instance.
- K3 Optical balance. Padding is even on both sides of a container, and icons sit centred in their buttons.
- K4 Wrapping. No awkward wrap, truncation, or orphan in a title, label, or button.
- K5 Finish. Nothing looks unfinished or placeholder, and no rendering artefact appears.

## Required output

Plain text, in this order.

1. `OVERALL: <score>/10`
2. `DIMENSIONS:` seven lines, `<name>: <score>`.
3. `CRITERIA:` all 35 lines in rubric order, `<id> <score> <instances with file names, or "none">`.
4. `ISSUES:` every instance from the criteria list, ranked by how much it hurts a user, most damaging first. For
   each: the criterion id, the file names, what is wrong in one or two sentences, and a concrete fix.
5. `FASTEST GAINS:` the five criteria with the lowest scores, lowest first.
6. `WORKING WELL:` up to five things that must not regress.
