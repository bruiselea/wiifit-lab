# Design QA — 3Dバランス・テーブル

- source visual truth: temporary reference screenshot used during QA (not included in this repository)
- implementation screenshot path: `E:\WiiFitLab\work\table3d-aurora-active.png`
- combined full-view evidence: `E:\WiiFitLab\work\design-comparison-aurora.png`
- viewport: native WPF window, 1130 × 770 px
- source pixels: 640 × 480 px
- implementation pixels: 1130 × 770 px
- comparison pixels: 1600 × 680 px; each complete artifact was proportionally scaled without density-based resampling assumptions
- state: active gameplay, level 3, three balls and three holes visible

## Findings

No actionable P0, P1, or P2 findings remain.

- Fonts and typography: the Japanese Yu Gothic UI hierarchy is legible at native size. The compact level/time/remaining pills preserve the source hierarchy without copying its exact lettering.
- Spacing and layout rhythm: the fixed-perspective S-shaped table dominates the play area; the separate control rail keeps live input and safety controls readable without obscuring the board.
- Colors and visual tokens: cream tile tops and brown edges retain the physical-table metaphor. The former padded-square wall was replaced with a deliberately original deep-navy aurora environment at the user's request.
- Image quality and asset fidelity: the new aurora is a full-resolution generated raster resource with no visible stretching artifacts at the target viewport. The board, balls, holes, barriers, and HUD remain sharp WPF-rendered geometry.
- Copy and content: instructions describe the implemented mechanic—tilting the table itself—and include both board and keyboard controls.

## Open Questions

- None. The background divergence is intentional and explicitly requested, while the reference is used only for composition and interaction structure.

## Focused Region Evidence

A separate crop was not needed: the 1130 × 770 native capture keeps the HUD, table edges, tile grid, holes, balls, and full background treatment readable in the combined evidence. The right-side controls were also checked at native size in the implementation capture.

## Comparison History

1. Earlier implementation used a cyan/green padded-square wall that tracked the reference too closely.
2. Fix: replaced the asset and resource reference with an original dark-space aurora scene; increased the dark overlay to keep the cream platform readable.
3. Post-fix evidence: `E:\WiiFitLab\work\table3d-aurora-active.png` and `E:\WiiFitLab\work\design-comparison-aurora.png` show the new environment while preserving the intended fixed-camera 3D table layout.

## Follow-up Polish

- P3: optional subtle contact shadows could add more depth under balls and barriers, but this does not affect clarity or the requested visual direction.

## Implementation Checklist

- [x] Replace close-copy wall texture with original artwork.
- [x] Preserve board contrast and HUD readability.
- [x] Verify active gameplay state with three balls and multiple holes.
- [x] Compare reference and native implementation in one combined image.

final result: passed
