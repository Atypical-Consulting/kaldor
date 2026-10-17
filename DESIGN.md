---
name: Kaldor
description: Kaldor explained like a well-made appliance, one exact line drawing of the mechanism with every paragraph keyed to a numbered disc.
colors:
  ground: "#e7e7e3"
  paper: "#f4f4f1"
  well: "#dcdcd7"
  line: "#cbcbc5"
  drawing-soft: "#9a9ba0"
  mid: "#5d5e64"
  ink-2: "#4a4b4f"
  ink: "#2b2c2e"
  green: "#2f7a41"
  red: "#b0391f"
typography:
  display:
    fontFamily: "Hanken Grotesk, system-ui, sans-serif"
    fontSize: "clamp(32px, 4vw, 42px)"
    fontWeight: 600
    lineHeight: 1.1
    letterSpacing: "-0.02em"
  headline:
    fontFamily: "Hanken Grotesk, system-ui, sans-serif"
    fontSize: "clamp(26px, 3vw, 32px)"
    fontWeight: 600
    lineHeight: 1.15
    letterSpacing: "-0.015em"
  title:
    fontFamily: "Hanken Grotesk, system-ui, sans-serif"
    fontSize: "18px"
    fontWeight: 600
    lineHeight: 1.3
    letterSpacing: "0"
  lead:
    fontFamily: "Hanken Grotesk, system-ui, sans-serif"
    fontSize: "19px"
    fontWeight: 400
    lineHeight: 1.55
  body:
    fontFamily: "Hanken Grotesk, system-ui, sans-serif"
    fontSize: "16.5px"
    fontWeight: 400
    lineHeight: 1.65
    fontFeature: "tnum"
  note:
    fontFamily: "Hanken Grotesk, system-ui, sans-serif"
    fontSize: "14.5px"
    fontWeight: 400
    lineHeight: 1.65
  label:
    fontFamily: "Geist Mono, ui-monospace, monospace"
    fontSize: "12px"
    fontWeight: 500
    lineHeight: 1.4
    letterSpacing: "0.06em"
  code:
    fontFamily: "Geist Mono, ui-monospace, monospace"
    fontSize: "13.5px"
    fontWeight: 400
    lineHeight: 1.6
  disc-numeral:
    fontFamily: "Geist Mono, ui-monospace, monospace"
    fontSize: "11px"
    fontWeight: 600
    lineHeight: 1
rounded:
  hairline: "3px"
  panel: "4px"
  disc: "50%"
spacing:
  gutter: "clamp(20px, 4vw, 48px)"
  index-column: "248px"
  header: "64px"
  section-top: "48px"
  section-bottom: "56px"
  measure: "68ch"
  measure-lead: "60ch"
  wide-max: "1080px"
components:
  disc:
    textColor: "{colors.ink}"
    typography: "{typography.disc-numeral}"
    rounded: "{rounded.disc}"
    size: "21px"
  disc-fill:
    backgroundColor: "{colors.ink}"
    textColor: "{colors.paper}"
    rounded: "{rounded.disc}"
    size: "21px"
  disc-go:
    backgroundColor: "{colors.green}"
    textColor: "#ffffff"
    rounded: "{rounded.disc}"
    size: "21px"
  disc-stop:
    backgroundColor: "{colors.red}"
    textColor: "#ffffff"
    rounded: "{rounded.disc}"
    size: "21px"
  disc-section:
    textColor: "{colors.ink}"
    rounded: "{rounded.disc}"
    size: "30px"
  step-marker:
    backgroundColor: "{colors.ink}"
    textColor: "{colors.paper}"
    typography: "{typography.disc-numeral}"
    rounded: "{rounded.disc}"
    size: "23px"
  figure-panel:
    backgroundColor: "{colors.paper}"
    rounded: "{rounded.panel}"
    padding: "10px 10px 14px"
  code-block:
    backgroundColor: "{colors.paper}"
    textColor: "{colors.ink}"
    typography: "{typography.code}"
    rounded: "{rounded.panel}"
    padding: "14px 18px"
  code-inline:
    backgroundColor: "{colors.well}"
    textColor: "{colors.ink}"
    rounded: "{rounded.hairline}"
    padding: "1px 5px"
  header:
    backgroundColor: "{colors.ground}"
    textColor: "{colors.ink}"
    height: "64px"
    padding: "0 clamp(20px, 4vw, 48px)"
  ref-table-header:
    textColor: "{colors.mid}"
    typography: "{typography.label}"
    padding: "0 16px 10px 0"
  contents-mobile:
    backgroundColor: "{colors.paper}"
    textColor: "{colors.ink}"
    rounded: "{rounded.panel}"
---

# Design System: Kaldor

## Overview

**Creative North Star: "The Instruction Leaflet"**

Kaldor is documented the way a well-made appliance is documented: one exact line drawing of its parts, and every paragraph keyed to a numbered disc on that drawing. The page is an enamel-grey leaflet with lighter paper panels, graphite ink and thin 1.5px line drawings. Nothing is decorated; the numbered disc is the only ornament, and colour appears only where a lamp would light on the real machine.

The reading model is reference first. A slim header, a sticky lettered contents column, one figure at a fixed scale, then keyed paragraphs, numbered procedures and label-grid reference tables. The same drawing returns for the refused state, stopped at the gate, rather than a new illustration. The system deliberately refuses the documentation-site default of sidebar tree, blue-accent cards and dark code slabs.

**Key Characteristics:**
- Neutral enamel grey ground (kept clear of cream), lighter paper panels for anything you read closely or copy.
- Graphite ink everywhere; green and red appear only as indicators.
- Numbered discs key text to the drawing; letters key sections, digits key parts of the mechanism.
- One line drawing, drawn once, recoloured by state.
- Flat: hairline borders, no shadows.
- Works without JavaScript; the script only marks the current section and plays the figure once.

## Colors

A near-neutral grey leaflet in graphite, with one indicator green and one signal red held in reserve.

### Primary
- **Indicator Green** (green): the lamp colour. Lit discs (`go`), the gate's pass lamp, the travelling commit in Fig. 1, the newest commit in the repository, the wordmark's indicator dot, the favicon's dot, the keyboard focus ring, and passing lines in a quoted report.

### Secondary
- **Signal Red** (red): the refusal colour, and nothing else. The stop disc, the lit stop lamp, the refused return path and its label in Fig. 2, and `BLOCKED` in a quoted report.

### Neutral
- **Enamel Ground** (ground): page background and the translucent sticky header. A cool, low-chroma grey; never warm it toward cream.
- **Leaflet Paper** (paper): figure panels, code blocks, the mobile contents drawer, and the numeral colour inside filled discs.
- **Screen Well** (well): inline code chips and the "screen" insets drawn inside Claude Code and the reviewer in the figure.
- **Hairline** (line): every border and divider: header rule, section rules, panel borders, table rows, step rules.
- **Drawing Soft** (drawing-soft): secondary strokes inside the figure: dashed data paths, callout leaders, unlit stop lamp, the return path at rest.
- **Mid Graphite** (mid): captions, labels, notes, table headers, the edition line, the scroll cue.
- **Soft Ink** (ink-2): the lead paragraph, the "who runs it" markers, footer links.
- **Graphite Ink** (ink): all body text, headings, links, primary figure strokes, filled discs and step markers.

### Named Rules
**The Lamp Rule.** Green means "this part is live or passed". It lights discs, lamps, the commit in transit and the focus ring; it never colours links, headings, panels or backgrounds.

**The Reserved Red Rule.** Red appears only when something is refused. A page with no refusal on it carries no red.

**The Enamel Ground Rule.** The ground is the confirmed neutral enamel grey. Warmer, creamier or paper-white grounds are a different world.

## Typography

**Body Font:** Hanken Grotesk (with system-ui, sans-serif)
**Label/Mono Font:** Geist Mono (with ui-monospace, monospace)

**Character:** A plain, slightly technical grotesk for everything read, and a crisp mono for everything typed, named or captioned, the way a manual sets running text against part numbers.

### Hierarchy
- **Display** (600, clamp(32px, 4vw, 42px), 1.1, -0.02em): the one page title under Fig. 1.
- **Headline** (600, clamp(26px, 3vw, 32px), 1.15, -0.015em): section titles, each led by a 30px lettered disc.
- **Title** (600, 18px, 1.3): sub-section heads and the keyed-paragraph heads beside their 21px discs.
- **Lead** (400, 19px, 1.55, max 60ch, Soft Ink): one sentence under the title.
- **Body** (400, 16.5px, 1.65, max 68ch): running text, with tabular numerals set globally. Keyed paragraphs drop to 15.5px; notes to 14.5px in Mid Graphite.
- **Label** (Geist Mono 500, 12px, uppercase, 0.06em to 0.12em): figure captions ("FIG. 1 — …"), the CONTENTS heading, table column headers.
- **Code** (Geist Mono 400, 13.5px, 1.6): code blocks; inline code at 0.86em on a well chip.

### Named Rules
**The Two Voices Rule.** If a reader would type it, path it or cite it as a figure, it is Geist Mono; if they would read it, it is Hanken Grotesk. No third face.

**The Weight Ceiling Rule.** Headings sit at 600; 700 belongs to the wordmark alone.

## Layout

A two-column leaflet: a sticky contents column (248px) beside a single reading column, under a 64px sticky header. Gutters scale with clamp(20px, 4vw, 48px). Prose holds to 68ch; figures, tables, procedures and code run wider, to roughly 1040 to 1080px. Sections are separated by a hairline rule with 48px above the next title and 56px below the last block. Anchors land 88px below the top so the header never covers a title.

Keyed paragraphs flow in an auto-fit grid (min 240px columns, 8px by 32px gap). Procedures use a strict margin column: a 40px track carries the step disc, the content sits in the second track, each step separated by a hairline.

**Responsive (900px and below):** the contents column folds into a collapsible "CONTENTS" drawer on paper at the top of the page; the edition line and optional header links hide; reference tables restack as one block per row. The figure is never redrawn or squeezed: it keeps a minimum width of 860px and scrolls sideways inside its panel, with a sticky "Scroll the figure →" cue above it and the caption pinned left. A portrait redraw is deferred.

### Named Rules
**The Fixed Scale Rule.** Every figure is the same 1120×486 drawing at the same scale. Variation comes from which parts are lit, never from re-composition.

## Elevation & Depth

Flat. There are no shadows anywhere. Depth is expressed by tone (paper panels sit one step lighter than the ground; the well sits one step darker) and by 1px hairlines. The only layered element is the sticky header, which is 92% ground with a light backdrop blur so text passing beneath it softens instead of colliding.

### Named Rules
**The Flat Leaflet Rule.** If a surface needs to stand apart, give it paper and a hairline. Never a shadow.

## Shapes

Small, quiet corners on HTML surfaces: 3px on inline code and the focus ring, 4px on panels (figures, code blocks, the mobile drawer). Discs are full circles with a 1.5px graphite ring (2px on the 30px section disc).

Inside the figure, modules carry rounder, appliance-like corners: 10px on the main units (Claude Code, the gate, the reviewer), 6px on the `.kaldor/` file block, 4px on drawn screens, and full pills for hooks (the PreToolUse hook, git pre-commit). All strokes are 1.5px with round caps and joins; arrowheads are small solid triangles that take the stroke colour.

## Components

### Numbered Disc (signature)
The system's only ornament and its indexing device.
- **Shape:** 21px circle, 1.5px graphite ring, Geist Mono 600 11px numeral centred.
- **Variants:** outline (default), filled (graphite with paper numeral), go (green), stop (red). A 30px variant with a 2px ring leads section titles.
- **Keying:** letters A to G identify sections (contents and section titles); digits 1 to 7 identify parts of the mechanism, in the order things happen, and the same digit appears on the figure callout and its keyed paragraph.
- **State:** in the desktop contents, the section in view fills its disc and sets its label to 600; the change eases over 0.25s.

### Figure Panel and Line Drawing (signature)
- **Panel:** paper, 1px hairline, 4px corners, 10px padding with 14px below; caption in the Label style beneath.
- **Drawing:** inline SVG, stroke currentColor at 1.5px, no fills except screens (well), lamps and the commit dots. Primary paths graphite, secondary paths Drawing Soft and dashed (4 5). Labels in Hanken 13px, captions 11.5px, code names Geist Mono 12px, secondary text Mid Graphite.
- **Callouts:** 12px-radius filled graphite circles with paper numerals, joined to their part by a short Drawing Soft leader. The pass step (6) is green.
- **Motion:** one authored moment. When Fig. 1 is half in view, a green commit travels the path once (3.4s, cubic-bezier(.33, 0, .2, 1), 0.5s delay) and the go lamp settles on. Without JavaScript or with reduced motion, the drawing is shown at rest with the lamp already lit.

### Refused State (signature)
The same drawing with a `refused` state, never a new illustration. The stop lamp lights red, the go lamp goes unlit, callout 6 turns red, the return path to Claude turns solid red with a red label, the path to the repository fades to 55% and dashes (3 5), the repository fades to 50%, and the newest commit empties. In motion, the commit travels only as far as the gate, the stop lamp settles at 2.6s and the return path appears at 2.8s.

### Procedure Steps
- **Style:** a borderless ordered list; each step a two-track grid (40px marker, content), separated by hairlines.
- **Marker:** a 23px filled graphite disc carrying the step numeral in Geist Mono.

### Code
- **Block:** paper, 1px hairline, 4px corners, 14px by 18px padding, horizontal scroll; comments in Mid Graphite. Never a dark slab.
- **Inline:** 0.86em Geist Mono on a well chip, 3px corners.
- **Quoted reports:** `BLOCKED` in red, passing lines in green, per the Lamp and Reserved Red rules.

### Reference Tables
- **Style:** full width to 1040px, collapsed borders; header row in the Label style over a 1.5px graphite rule; body rows separated by hairlines. The first column holds the command or key in unboxed Geist Mono 500 14px and does not wrap.
- **Who-runs-it marker:** an 8px ring before the label. Filled graphite for a human, open ring for the agent, dotted ring for a hook or git.
- **Narrow screens:** each row becomes a stacked block; the header row hides.

### Navigation
- **Header:** sticky, 64px, translucent ground with blur, hairline below. Wordmark in Hanken 700 19px followed by a 9px green indicator dot; the edition line ("Instructions for use · 1.0") in Mid Graphite 14px; links at 14.5px, plain until hover underlines them.
- **Contents:** "CONTENTS" in the Label style, then lettered discs with their section names; hover underlines.
- **Links in text:** graphite, underlined at 1px with a 3px offset; hover thickens the underline to 2px.
- **Focus:** a 2px green ring with 3px offset and 3px corners on every focusable element.

## Do's and Don'ts

### Do:
- **Do** key every explanatory paragraph to a numbered disc that also appears on the drawing.
- **Do** reuse the one figure at its fixed scale and change only what is lit to tell a different state.
- **Do** keep green for lamps, lit discs, the commit in transit and focus; keep red for refusal only.
- **Do** put anything copied or read closely (code, figures, the mobile contents) on paper with a hairline and 4px corners.
- **Do** set code, paths, figure captions and table labels in Geist Mono; everything else in Hanken Grotesk.
- **Do** let the figure scroll sideways on narrow screens, with the "Scroll the figure →" cue, rather than shrinking it.
- **Do** keep every page readable without JavaScript and without motion.

### Don't:
- **Don't** add shadows, gradients or coloured card backgrounds; depth is tone and hairline.
- **Don't** introduce a second accent, a blue link colour, or green on links and headings.
- **Don't** set code on dark slabs.
- **Don't** warm the ground toward cream or replace it with white.
- **Don't** draw a new illustration for a state the flow drawing can already show.
- **Don't** add icons or ornaments outside the circle family (numbered discs, lamps, who-runs-it rings, the indicator dot).
- **Don't** animate anything beyond the single commit-through-the-gate moment.
