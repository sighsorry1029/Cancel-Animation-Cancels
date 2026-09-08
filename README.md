## CancelAnimationCancels

### Blocks some known animation-cancel exploits such as block cancel, dodge cancel and emote cancel.

![](https://i.ibb.co/N6s7w3QM/emotecancel.gif) <br>
Emote cancel <br>
![](https://i.ibb.co/39WbbWXz/blockcancel.gif) <br>
Block cancel <br>
![](https://i.ibb.co/SwZdjLWq/dodgecancel.gif) <br>
dodge cancel <br>

- It does not try to globally disable actions like blocking, emoting, dodging, or key binds.
- Instead, it removes the specific state carry-over that makes an animation cancel profitable and only blocks narrow input patterns when a known exploit depends on them.

In practice, that means the mod watches for a short post-attack window, detects when a cancel action begins inside that window, and then forces the next attack to start as a fresh attack instead of continuing a combo or consuming a queued follow-up.

### What the mod is trying to stop

Some Valheim animation cancels do not simply shorten recovery. They allow the next attack to inherit combo state from the previous attack even though the player inserted another action in between.

That is the important distinction.

If the game still thinks the player is in the same combo chain after a block, emote, or dodge transition, the next attack can behave like a later combo hit even though the animation flow was interrupted. This can create cases where:

- later combo damage is reached too early,
- queued follow-up attacks survive through movement actions,
- or the player repeatedly accesses the stronger part of a chain without paying the normal timing cost.

This mod prevents those cases by resetting the internal carry-over used by the next `Attack.Start(...)`. <br>

### Config

```
[1 - General]

## If on, the configuration is locked and can be changed by server admins only. [Synced with Server]
# Setting type: Toggle
# Default value: On
# Acceptable values: Off, On
Lock Configuration = On

## If on, one-handed swords, knives, and one-handed clubs will lose combo carry when block is used between attacks. [Synced with Server]
# Setting type: Toggle
# Default value: On
# Acceptable values: Off, On
Prevent Block Combo Carry = On

## If on, recent attacks from affected weapons cannot be canceled into emotes, and combo carry will be reset if an emote still slips through. [Synced with Server]
# Setting type: Toggle
# Default value: On
# Acceptable values: Off, On
Prevent Emote Cancel = On

## If on, console bind commands cannot assign emotes, and existing emote binds are disabled until this is turned off. [Synced with Server]
# Setting type: Toggle
# Default value: Off
# Acceptable values: Off, On
Block Emote Console Binds = Off

## If on, unarmed, spears, axes, battleaxes, and atgeirs will lose queued attacks and combo carry when a dodge starts, and dodge-cancel attack input will be blocked. [Synced with Server]
# Setting type: Toggle
# Default value: On
# Acceptable values: Off, On
Prevent Dodge Attack Queue = On
```

### How the mod works internally

The mod uses a short reset window of `0.25` seconds after an attack. If a cancel action starts inside that window, the mod marks the combo as interrupted.

When the next attack starts, the mod forces a clean restart by doing two things:

1. It clears the inherited `previousAttack` reference.
2. It pushes `timeSinceLastAttack` slightly past the combo carry threshold.

This matters because Valheim's combo continuation logic depends on the next attack still seeing valid previous attack state inside a short timing window. If that state is removed, the next attack becomes a normal first hit instead of continuing into a stronger or queued follow-up.

Several options also clear `m_nextAttackChainLevel` or queued attack timers immediately when the cancel action begins. That prevents the exploit from surviving until the next input.

## Option details

### Prevent Block Combo Carry

This option targets block-based combo abuse.

Affected weapons:

- One-handed swords
- Knives
- One-handed clubs

What the exploit looks like:

- the player lands an early combo hit,
- starts blocking during the short combo continuation window,
- then attacks again immediately after the block animation begins.

Why that can be abused:

- the inserted block animation visually interrupts the flow,
- but the next attack may still inherit combo progression from the previous attack,
- so the player reaches later chain damage sooner than intended or repeats a stronger part of the combo too quickly.

What this option does:

- it watches for the transition from "not blocking" to "blocking",
- checks whether that block started within `0.25` seconds of the last attack,
- and only applies to the affected weapon set above.

When that happens, the mod:

- marks that the combo was interrupted by block,
- clears `m_nextAttackChainLevel` on the current and previous attack objects,
- and then, on the next `Attack.Start(...)`, removes combo inheritance so the next swing starts from hit 1 instead of carrying the previous chain forward.

Why this is the preferred fix:

- blocking itself is not disabled,
- the player can still defend normally,
- but the block can no longer be used as a bridge that preserves attack combo state through an interruption.

### Prevent Emote Cancel

This option targets emote-based attack cancel abuse.

Affected attacks:

- One-handed sword attacks
- One-handed axe attacks
- Knife attacks
- Dual axe attacks
- Two-handed axe / battleaxe attacks
- Spear primary attack only

What the exploit looks like:

- the player attacks,
- triggers an emote during a narrow timing window,
- and then attacks again as the emote interrupts or truncates the normal recovery.

Why that can be abused:

- the emote changes the animation state,
- but the game may still treat the next attack as part of the previous combo sequence,
- allowing the player to skip normal pacing and keep access to later combo progression.

This option uses two layers of protection.

First layer: prevent the emote from starting.

- After every successful attack, the mod records whether that attack belongs to a weapon set that is vulnerable to emote cancel abuse.
- If the player tries to start an emote within `0.25` seconds of one of those attacks, `Player.StartEmote(...)` is blocked.

This stops the most direct version of the exploit before the animation transition even begins.

Second layer: reset combo carry if an emote still happens.

Some emote transitions can happen through paths other than the exact `StartEmote` case you expect, or another mod could interfere with the flow. Because of that, the mod also watches `Player.UpdateEmote(...)`.

If the player enters an emote state during the vulnerable post-attack window, the mod marks the combo as interrupted by emote. Then the next `Attack.Start(...)` is forced to begin as a fresh combo instead of inheriting the previous chain.

Why this option exists separately from bind blocking:

- an emote can be triggered in several ways,
- `/bind` is only one input route,
- so the real exploit fix is to block or invalidate the emote-based combo carry itself.

### Block Emote Console Binds

This option targets the most convenient delivery mechanism for many emote cancels: console binds such as `bind e challenge`.

What the exploit looks like:

- the player binds an emote to a key,
- repeatedly alternates that key with attack input,
- and uses the bound emote to clip recovery or preserve unintended combo flow.

Why this option is not the main fix on its own:

- typing an emote command directly is still conceptually possible,
- other UI paths may still trigger emotes,
- and the real gameplay problem is the combo carry after the emote, not the existence of key binding itself.

What this option does:

- it intercepts `Terminal.TryRunCommand(...)`,
- detects `bind` commands,
- extracts the command being assigned,
- and rejects the bind if the target command is an emote.

While the option is enabled, it also refreshes the game's bind table and removes emote commands from the active runtime bind map. This means:

- new emote binds cannot be created,
- and existing emote binds are filtered out while the option is active.

Recommended use:

- leave `Prevent Emote Cancel` enabled as the real gameplay fix,
- then use `Block Emote Console Binds` if you also want to remove the easiest input method for repeating the exploit.

### Prevent Dodge Attack Queue

This option targets dodge-based queue carry and a specific dodge-cancel input pattern.

Affected weapon skills:

- Unarmed
- Spears
- Axes
- Polearms

In practical terms, this is intended to cover the exploit families reported for:

- unarmed,
- spear,
- one-handed axe,
- battleaxe,
- and atgeir.

What the exploit looks like:

- the player keeps attack-related inputs held,
- starts a dodge at a precise timing,
- and uses the dodge transition to cancel the recovery while preserving queued attack state or combo progression.

Why that can be abused:

- dodge changes movement and animation state,
- but queued attack timers or combo carry can still survive long enough to produce an unnaturally fast follow-up,
- and some reported setups rely on holding attack and block together while triggering dodge.

What this option does:

- it watches for the moment the player actually enters dodge,
- only applies to the affected weapon set,
- and immediately clears both queued attack timers:
  - `m_queuedAttackTimer`
  - `m_queuedSecondAttackTimer`

If the dodge started within `0.25` seconds of the previous attack, the mod also:

- marks that the combo was interrupted by dodge,
- clears `m_nextAttackChainLevel` on the current and previous attack objects,
- and forces the next `Attack.Start(...)` to begin as a fresh combo rather than continuing the earlier chain.

In addition, the mod blocks a narrow dodge-cancel input pattern while the dodge is being requested, queued, or actively playing:

- if the player is holding block and primary attack together,
- and a dodge is being triggered or is already in progress,
- the primary attack input is suppressed and queued attack timers are cleared again.

This is intentionally narrower than globally blocking all attacks during dodge. The goal is to stop the known exploit pattern without making normal post-dodge attack timing feel unnecessarily heavy.

## Relationship between the options

These options are complementary, not redundant.

- `Prevent Block Combo Carry` stops combo progression from surviving through block startup.
- `Prevent Emote Cancel` stops emotes from being used as a combo-preserving interrupt.
- `Block Emote Console Binds` removes a common emote exploit input route, but does not replace the gameplay fix.
- `Prevent Dodge Attack Queue` clears queued and carried attack state when dodge begins and blocks the known dodge-cancel input pattern.

Together, they cover several different ways players can interrupt animation flow while trying to keep the part that matters most: attack progression.

## What this mod does not do

This mod does not try to remove every fast input sequence in the game.

Its goal is narrower:

- stop block, emote, and dodge transitions from preserving attack state in ways that create unintended damage progression or queued follow-ups,
- while avoiding blanket bans on normal actions whenever possible.

If a future exploit uses a different state transition, it should be fixed the same way: identify which piece of attack state is surviving across the interruption, then clear that state instead of disabling unrelated systems globally.
