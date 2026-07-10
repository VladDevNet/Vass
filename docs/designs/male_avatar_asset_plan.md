# Male Avatar v1 Asset Plan

Accepted source concept:

```text
docs/designs/male_avatar_concept_v1.png
```

Cut guide:

```text
docs/designs/male_avatar_cut_guide_v1.png
```

AI expression sources:

```text
docs/designs/male_expression_closed_eyes_v1.png
docs/designs/male_expression_mouth_small_v1.png
docs/designs/male_expression_mouth_big_v1.png
```

Current preview:

```text
docs/designs/male_avatar_ai_expression_preview_v1.png
```

## Direction

Use the AI-generated male portrait as the matching visual counterpart to the accepted Olga avatar.

The male set follows the same MVP layered PNG strategy:

1. Base image: full male portrait.
2. Blink overlay: closed eyelids sliced from the closed-eyes AI source.
3. Mouth overlays: open mouth shapes sliced from matching speaking AI sources.
4. Glow overlay: state-colored halo implemented in React Native first.
5. Subtle transform animation: breathing scale, tiny head bob, glow pulse.

## Initial Layers

| Layer | Purpose |
| --- | --- |
| `male_base.png` | Full accepted portrait, used as the visual base |
| `male_eyes_closed_overlay.png` | Eyelid overlay for blink/sleep |
| `male_mouth_open_small_overlay.png` | Speaking loop frame 1 |
| `male_mouth_open_big_overlay.png` | Speaking loop frame 2 |

## Mobile Asset Paths

```text
mobile/assets/avatar/male_base.png
mobile/assets/avatar/male_eyes_closed_overlay.png
mobile/assets/avatar/male_mouth_open_small_overlay.png
mobile/assets/avatar/male_mouth_open_big_overlay.png
mobile/assets/avatar/source/male_expression_closed_eyes_v1.png
mobile/assets/avatar/source/male_expression_mouth_small_v1.png
mobile/assets/avatar/source/male_expression_mouth_big_v1.png
```

## State Mapping

| Voice state | Visual behavior |
| --- | --- |
| `idle` | Base portrait, soft gold glow, slow breathing |
| `recording` | Blue/cyan glow pulse, attentive eyes |
| `thinking` | Purple glow, subtle head bob |
| `speaking` | Gold glow, mouth overlay cycles small/big open |
| `paused` | Dim base, low glow |
| `sleeping` | Closed eyes overlay, slow breathing |
