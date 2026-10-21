# Changelog

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
