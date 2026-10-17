---
version: 1
slug: "site-index-html"
primary_target: "site/index.html"
related_targets: ["site"]
---

# Kaldor documentation site

Scope: the whole documentation site in `site/` (index plus reference pages). Visitor mode: Read.

Audience and job: developers who already use Claude Code, adopting Kaldor. They come to understand the flow, install it, then return to look up commands, rules and what to do when a commit is refused. Proof and content: real reviewer outputs and early measurements from PRODUCT.md; no invented claims.

Constraints: static HTML/CSS, no build step, served from GitHub Pages under /kaldor/; fonts may load from Google Fonts; must work without JavaScript.

## Direction contract

THESIS: Kaldor explained the way a well-made appliance is explained: one exact line drawing of its parts, every paragraph keyed to a numbered disc. It refuses the category default of sidebar tree plus blue-accent cards plus dark code slabs.

OWN-WORLD: neutral enamel grey ground (#e7e7e3, kept clear of cream), slightly lighter paper panels, graphite ink, one indicator green used only inside numbered discs and lamps, a signal red reserved for the refused state. Hanken Grotesk for everything read, Geist Mono for code, paths and figure captions. Thin 1.5px graphite line drawings, rounded module corners, numbered discs as the only ornament.

STORY: the reader sees the whole mechanism in one drawing, reads why each numbered part exists, installs in two commands, and keeps the commands and rules pages as reference.

FIRST VIEWPORT: slim header (wordmark with green indicator dot, "Instructions for use · version", top links); left contents column numbered 1–7 in discs; Fig. 1, the flow drawing with callouts 1–5, full width of the reading area; below it the title "How Kaldor works", a lead sentence, and three keyed paragraphs (2, 4, 5).

FORM: instruction leaflet, candidate 6 of 7 on the grounded list. seed:b0c4e0ac400e9c92. Raises: the flow figure recurs at one fixed scale with only the relevant part lit; colour lives only in discs and indicators; procedures numbered in a strict margin column; a refused commit is drawn as the flow stopped at the reviewer.

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance

## Unresolved

- No logo exists; the wordmark plus indicator dot stands in.
