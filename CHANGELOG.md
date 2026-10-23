# Changelog

## 1.1.2

- Weapon sway: on fast turns the arms and the weapon trail behind a little and settle back smoothly, instead of being locked to the screen. Weapon Sway sets how much, 0 turns it off.
- The body is turned once per frame, from that frame's view, and the arms aim from the same frame: no more jitter of the arms while turning fast.
- Effects that go off in the middle of your own body, like the flash of Huntress' blink and of the teleport on her special, are hidden in first person (Hide Bursts At Your Body). So are the flashes the game draws over your own model, like Huntress reappearing after her blink, which lit up her eyes right at the camera.
- Glowing weapon parts made of several pieces, like Huntress' arrow cluster on her special, fade as one piece and get out of the way sooner. When a skill switches them on, they stay hidden until measured, so they don't flash up in front of the eyes.
- While a skill moves the whole body (Huntress' Arrow Rain and glaive throw, Mercenary's dashes, Loader's charged punches), your body and weapon are hidden instead of passing through the camera. Primary attacks keep them visible, so melee swings stay on screen. Hide Body During Skills turns it off.
- The first person key reacts while other keys are held (running, sprinting). During the drop pod or a cutscene it switches whether first person comes back afterwards.

## 1.1.1

- Captain's coat and body (his model has the head separate) count as the body now: his gun stays visible and only fades when it covers a big part of the screen.
- The check that the screen coverage measurement works no longer gives up by mistake when big meshes are around the camera.
- Skills and rides that widen the game's field of view (Enforcer's dash and shield) widen first person only as much as sprinting does, scaled by Sprint FOV Effect. Scopes still zoom in fully.

## 1.1.0

- First public release.
- Uses the game's own camera: cutscenes, the drop pod, death and spectating show the vanilla view, and first person comes back afterwards.
- Weapon lowering for every survivor, on by default for Railgunner.
- Headgear is hidden in first person: hats, caps, glasses and eye glows that would sit in front of the camera.
- The camera never sits behind the eyes, also for faces that stick out of the head's shape (Operator).
- Rides that keep the body in view (Enforcer's dash, ziplines) stay in first person; the drop pod and rides that hide the body show the vanilla camera.
- The first person key always reacts; it is only ignored while typing in the chat or the console.
- Aiming from the eyes in multiplayer as well.
- Faded weapon parts are hidden on the game's no-draw layer, so they stay hidden reliably.
- Camera height per survivor in centimeters.
- Old settings are carried over and settings that no longer exist are removed from the config file.
