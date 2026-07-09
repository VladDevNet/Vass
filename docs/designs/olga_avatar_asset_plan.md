# Olga Avatar v1 Asset Plan

Accepted source concept:

```text
docs/designs/olga_avatar_concept_v1.png
```

Cut guide:

```text
docs/designs/olga_avatar_cut_guide_v1.png
```

AI expression sources:

```text
docs/designs/olga_expression_closed_eyes_v1.png
docs/designs/olga_expression_mouth_small_v1.png
docs/designs/olga_expression_mouth_big_v1.png
```

Current preview:

```text
docs/designs/olga_avatar_ai_expression_preview_v1.png
```

## Direction

Use the accepted AI-generated Olga portrait as the visual source for the first beautiful avatar release.

The first implementation should not wait for a perfect Rive file. We can start with a layered PNG avatar in React Native, then move the same expression set into Rive later.

## MVP Animation Strategy

Keep the base portrait mostly intact and animate lightweight overlays generated from matching AI expression images:

1. Base image: full Olga portrait.
2. Blink overlay: closed eyelids sliced from the closed-eyes AI source.
3. Mouth overlays: open mouth shapes sliced from matching speaking AI sources.
4. Glow overlay: state-colored halo, implemented either as PNG or React Native shadow/gradient.
5. Subtle transform animation: breathing scale, tiny head bob, glow pulse.

This is much faster than trying to perfectly cut and repaint every hidden facial region.

The earlier rough hand-made overlays are superseded by the AI-sliced overlay set.

## Initial Layers

Minimum useful layer set:

| Layer | Purpose |
| --- | --- |
| `olga_base.png` | Full accepted portrait, used as the visual base |
| `olga_eyes_closed_overlay.png` | Eyelid overlay for blink/sleep |
| `olga_mouth_open_small_overlay.png` | Speaking loop frame 1 |
| `olga_mouth_open_big_overlay.png` | Speaking loop frame 2 |
| `olga_glow_gold.png` | Speaking / warm idle glow |
| `olga_glow_blue.png` | Listening glow |
| `olga_glow_purple.png` | Thinking glow |

Later, if the result is worth polishing:

- `hair_front.png`
- `hair_back.png`
- `face_base.png`
- `brows_neutral.png`
- `brows_thinking.png`
- `eyes_open.png`

## State Mapping

| Voice state | Visual behavior |
| --- | --- |
| `idle` | Base portrait, soft gold glow, slow breathing |
| `recording` | Blue/cyan glow pulse, attentive eyes |
| `thinking` | Purple glow, subtle eye/brow overlay if available |
| `speaking` | Gold glow, mouth overlay cycles small/big open |
| `paused` | Dim base, low glow |
| `sleeping` | Closed eyes overlay, slow breathing |

## Next Step

Validate the AI-sliced overlay set in a quick React Native prototype:

1. Closed eyelids from `olga_expression_closed_eyes_v1.png`.
2. Open-small and open-big mouth overlays from the speaking sources.
3. Glow variants from React Native shadow/gradient first; PNG glow only if runtime styling is not enough.

If the prototype feels alive, rebuild the same states in Rive for the polished release.
