using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace MegaBuilder
{
    [HarmonyPatch]
    public static class GridAlignment
    {
        private static bool _alignToggled;
        private static int _defaultAlignment = 100; // centimeters: 50=0.5, 100=1, 200=2, 400=4
        private static readonly int[] AlignmentSteps = { 50, 100, 200, 400 };

        private static readonly FieldInfo _placementGhostField =
            AccessTools.Field(typeof(Player), "m_placementGhost");

        // Saved vanilla position for doors — captured right after vanilla's method,
        // before other mods (e.g. PerfectPlacement) can grid-snap it
        private static Vector3 _savedDoorPosition;
        private static Quaternion _savedDoorRotation;
        private static bool _hasSavedDoorPosition;

        // Throttle debug logging to avoid spam (log every N frames while holding piece)
        private static int _debugFrameCounter;
        private static string _lastGhostName;

        private static bool Debug => MegaBuilderPlugin.DebugMode.Value;

        private static void DebugLog(string msg)
        {
            if (Debug) MegaBuilderPlugin.Log.LogInfo($"[GridAlign] {msg}");
        }

        [HarmonyPatch(typeof(Player), "Update")]
        [HarmonyPostfix]
        private static void PlayerUpdate_Postfix(Player __instance)
        {
            if (!MegaBuilderPlugin.EnableGridAlignment.Value) return;
            if (__instance != Player.m_localPlayer) return;
            if (Chat.instance?.HasFocus() == true) return;
            if (Console.IsVisible()) return;
            if (Menu.IsVisible()) return;

            // F7 - Toggle grid alignment
            if (Input.GetKeyDown(MegaBuilderPlugin.GridToggleKey.Value))
            {
                _alignToggled = !_alignToggled;
                string state = _alignToggled ? "ON" : "OFF";
                __instance.Message(MessageHud.MessageType.TopLeft,
                    $"Grid alignment: {state} (size: {_defaultAlignment / 100f})");
                DebugLog($"Grid alignment toggled: {state}");
            }

            // F6 - Cycle grid size
            if (Input.GetKeyDown(MegaBuilderPlugin.GridSizeCycleKey.Value))
            {
                int idx = System.Array.IndexOf(AlignmentSteps, _defaultAlignment);
                idx = (idx + 1) % AlignmentSteps.Length;
                _defaultAlignment = AlignmentSteps[idx];
                __instance.Message(MessageHud.MessageType.TopLeft,
                    $"Grid size: {_defaultAlignment / 100f}");
                DebugLog($"Grid size cycled to: {_defaultAlignment / 100f}");
            }
        }

        /// <summary>
        /// FIRST postfix — runs immediately after vanilla's UpdatePlacementGhost,
        /// before other mods (like PerfectPlacement) can modify the position.
        /// Captures the vanilla snap position for doors so we can restore it later.
        /// </summary>
        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.First)]
        private static void UpdatePlacementGhost_SaveVanilla(Player __instance)
        {
            _hasSavedDoorPosition = false;
            if (__instance != Player.m_localPlayer) return;

            var ghost = _placementGhostField?.GetValue(__instance) as GameObject;
            if (ghost == null || !ghost.activeSelf) return;

            if (ghost.GetComponentInChildren<Door>() != null)
            {
                _savedDoorPosition = ghost.transform.position;
                _savedDoorRotation = ghost.transform.rotation;
                _hasSavedDoorPosition = true;
                DebugLog($"[SaveVanilla] Saved door pos: ({_savedDoorPosition.x:F3}, {_savedDoorPosition.y:F3}, {_savedDoorPosition.z:F3})");
            }
        }

        /// <summary>
        /// LAST postfix — runs after ALL other mods' postfixes on UpdatePlacementGhost.
        /// For doors: restores the vanilla snap position (undoing PerfectPlacement etc.)
        /// For other pieces: applies MegaBuilder's grid alignment.
        /// </summary>
        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void UpdatePlacementGhost_ApplyGrid(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;

            var ghost = _placementGhostField?.GetValue(__instance) as GameObject;
            if (ghost == null || !ghost.activeSelf) return;

            var piece = ghost.GetComponent<Piece>();
            if (piece == null) return;

            // Throttle debug: log details when piece changes or every 60 frames
            bool shouldLogDetails = false;
            if (Debug)
            {
                _debugFrameCounter++;
                string currentName = ghost.name;
                if (currentName != _lastGhostName)
                {
                    _lastGhostName = currentName;
                    _debugFrameCounter = 0;
                    shouldLogDetails = true;
                }
                else if (_debugFrameCounter % 60 == 0)
                {
                    shouldLogDetails = true;
                }
            }

            if (shouldLogDetails)
            {
                var components = ghost.GetComponents<Component>();
                var componentNames = string.Join(", ", components.Select(c => c.GetType().Name));
                DebugLog($"Ghost: '{ghost.name}' | Components: [{componentNames}]");

                var childComponents = ghost.GetComponentsInChildren<Component>(true);
                var uniqueChildTypes = new HashSet<string>();
                foreach (var c in childComponents)
                    uniqueChildTypes.Add(c.GetType().Name);
                DebugLog($"  All child component types: [{string.Join(", ", uniqueChildTypes)}]");

                var doorComp = ghost.GetComponentInChildren<Door>();
                DebugLog($"  Door component: {(doorComp != null ? $"FOUND on '{doorComp.gameObject.name}'" : "NOT FOUND")}");

                var snapPoints = new List<Transform>();
                piece.GetSnapPoints(snapPoints);
                DebugLog($"  Snap points: {snapPoints.Count}");
                foreach (var sp in snapPoints)
                {
                    var localPos = Quaternion.Inverse(piece.transform.rotation) * (sp.position - piece.transform.position);
                    DebugLog($"    Snap point '{sp.name}': local=({localPos.x:F3}, {localPos.y:F3}, {localPos.z:F3})");
                }

                DebugLog($"  Ghost position (post-all-mods): ({ghost.transform.position.x:F3}, {ghost.transform.position.y:F3}, {ghost.transform.position.z:F3})");
                DebugLog($"  Ghost rotation: ({ghost.transform.rotation.eulerAngles.x:F1}, {ghost.transform.rotation.eulerAngles.y:F1}, {ghost.transform.rotation.eulerAngles.z:F1})");
                if (_hasSavedDoorPosition)
                    DebugLog($"  Saved vanilla door pos: ({_savedDoorPosition.x:F3}, {_savedDoorPosition.y:F3}, {_savedDoorPosition.z:F3})");
            }

            // DOORS: Restore vanilla's snap position, undoing any modifications
            // from other mods (e.g. PerfectPlacement's grid alignment).
            // The vanilla position was captured in the Priority.First postfix before
            // other mods had a chance to modify it.
            if (_hasSavedDoorPosition && ghost.GetComponentInChildren<Door>() != null)
            {
                var preRestore = ghost.transform.position;
                ghost.transform.position = _savedDoorPosition;
                ghost.transform.rotation = _savedDoorRotation;
                if (shouldLogDetails)
                {
                    var delta = preRestore - _savedDoorPosition;
                    DebugLog($"  >> DOOR RESTORED: vanilla pos restored (other mod moved it by {delta.magnitude:F3}m)");
                }
                return;
            }

            // Non-door pieces: apply MegaBuilder grid alignment if enabled
            if (!MegaBuilderPlugin.EnableGridAlignment.Value) return;
            if (!_alignToggled) return;

            if (shouldLogDetails) DebugLog($"  >> APPLYING grid snap (grid size: {_defaultAlignment / 100f})");
            SnapToGrid(ghost, piece, shouldLogDetails);
        }

        private static void SnapToGrid(GameObject ghost, Piece piece, bool debugLog)
        {
            // Based on PerfectPlacement's proven grid alignment algorithm:
            // 1. Convert world position to piece-local space (rotation-aware)
            // 2. Compute per-axis alignment from snap point bounding box
            // 3. Round each axis to nearest grid increment
            // 4. Convert back to world space

            Vector3 pos = piece.transform.position;
            Quaternion rot = piece.transform.rotation;

            // Convert to piece-local space
            Vector3 localPos = Quaternion.Inverse(rot) * pos;

            // Get alignment sizes and offsets from snap points
            GetAlignment(piece, out Vector3 alignment, out Vector3 offset, debugLog);

            // Add offset so snapping aligns to snap point positions
            localPos += offset;

            // Save pre-snap for axes we won't touch
            Vector3 preSnap = localPos;

            // Snap each axis that has a positive alignment
            if (alignment.x > 0f)
                localPos.x = Mathf.Round(localPos.x / alignment.x) * alignment.x;
            if (alignment.y > 0f)
                localPos.y = Mathf.Round(localPos.y / alignment.y) * alignment.y;
            if (alignment.z > 0f)
                localPos.z = Mathf.Round(localPos.z / alignment.z) * alignment.z;

            // Restore axes with zero alignment
            if (alignment.x <= 0f) localPos.x = preSnap.x;
            if (alignment.y <= 0f) localPos.y = preSnap.y;
            if (alignment.z <= 0f) localPos.z = preSnap.z;

            // Remove offset
            localPos -= offset;

            // Convert back to world space
            Vector3 finalPos = rot * localPos;

            if (debugLog)
            {
                DebugLog($"  Alignment: ({alignment.x:F3}, {alignment.y:F3}, {alignment.z:F3})");
                DebugLog($"  Offset: ({offset.x:F3}, {offset.y:F3}, {offset.z:F3})");
                DebugLog($"  Pre:  ({pos.x:F3}, {pos.y:F3}, {pos.z:F3})");
                DebugLog($"  Post: ({finalPos.x:F3}, {finalPos.y:F3}, {finalPos.z:F3})");
            }

            piece.transform.position = finalPos;
        }

        private static float FixAlignment(float size)
        {
            int cm = (int)Mathf.Round(size * 100f);
            if (cm <= 0) return _defaultAlignment / 100f;
            if (cm <= 50) return 0.5f;
            if (cm <= 100) return 1f;
            if (cm <= 200) return 2f;
            return 4f;
        }

        private static void GetAlignment(Piece piece, out Vector3 alignment, out Vector3 offset, bool debugLog)
        {
            List<Transform> points = new List<Transform>();
            piece.GetSnapPoints(points);

            if (points.Count > 0)
            {
                Vector3 min = Vector3.positiveInfinity;
                Vector3 max = Vector3.negativeInfinity;
                foreach (Transform point in points)
                {
                    Vector3 lp = point.localPosition;
                    min = Vector3.Min(min, lp);
                    max = Vector3.Max(max, lp);
                }

                Vector3 size = max - min;
                alignment = new Vector3(FixAlignment(size.x), FixAlignment(size.y), FixAlignment(size.z));
                offset = max; // align at top/max

                if (debugLog)
                {
                    DebugLog($"  Snap bbox: min=({min.x:F3},{min.y:F3},{min.z:F3}) max=({max.x:F3},{max.y:F3},{max.z:F3})");
                    DebugLog($"  Snap size: ({size.x:F3},{size.y:F3},{size.z:F3}) -> align: ({alignment.x:F3},{alignment.y:F3},{alignment.z:F3})");
                }
            }
            else
            {
                // No snap points — use default grid for all axes
                float def = _defaultAlignment / 100f;
                alignment = new Vector3(def, def, def);
                offset = Vector3.zero;

                if (debugLog)
                    DebugLog($"  No snap points, using default: {def:F3} (all axes)");
            }
        }
    }
}
