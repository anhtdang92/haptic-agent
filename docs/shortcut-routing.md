# Controller shortcut routing

Controller actions in Mainframe must have one authoritative route.

- Raw controller events drive visualization and overlay navigation.
- Profile bindings drive agent and host actions, including `StartVoicePrompt`.
- A bare face-button handler must not duplicate a profile-bound action.
- Modified gestures win over their bare primary button through `MappingEngine` specificity ordering.

Regression case:

1. An approval is pending.
2. Hold `RB`.
3. Press `Y`.
4. The configured `RB+Y` approval command fires exactly once.
5. The voice overlay does not open.
6. After release, pressing bare `Y` still starts voice when the profile binds it to `StartVoicePrompt`.
