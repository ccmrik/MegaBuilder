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

                bool snappedToPiece = IsGhostSnappedToPiece(ghost, piece, true);
                DebugLog($"  IsGhostSnappedToPiece: {snappedToPiece}");
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

            // Skip grid snapping when vanilla has snap-connected this ghost to an
            // existing piece's snap point. This preserves wall-to-wall, corner, etc.
            // connections while allowing grid snapping when building near (but not
            // snapped to) existing pieces.
            if (IsGhostSnappedToPiece(ghost, piece, shouldLogDetails))
            {
                if (shouldLogDetails) DebugLog($"  >> SKIPPED: Ghost snap-connected to existing piece, using vanilla snapping");
                return;
            }

            if (shouldLogDetails) DebugLog($"  >> APPLYING grid snap (default align: {_defaultAlignment / 100f})");
            SnapToGrid(ghost, piece, shouldLogDetails);
        }

        /// <summary>
        /// Check if the ghost piece's snap points are aligned with any nearby
        /// placed piece's snap points. If so, vanilla has snap-connected them
        /// and we should not override with grid alignment.
        /// </summary>
        private static bool IsGhostSnappedToPiece(GameObject ghost, Piece ghostPiece, bool debugLog)
        {
            var ghostSnaps = new List<Transform>();
            ghostPiece.GetSnapPoints(ghostSnaps);
            if (ghostSnaps.Count == 0) return false;

            var nearbyPieces = new List<Piece>();
            Piece.GetAllPiecesInRadius(ghost.transform.position, 4f, nearbyPieces);

            const float snapTolerance = 0.05f;

            foreach (var placed in nearbyPieces)
            {
                if (placed == null || placed.gameObject == ghost) continue;

                var placedSnaps = new List<Transform>();
                placed.GetSnapPoints(placedSnaps);

                foreach (var gs in ghostSnaps)
                {
                    foreach (var ps in placedSnaps)
                    {
                        float dist = Vector3.Distance(gs.position, ps.position);
                        if (dist < snapTolerance)
                        {
                            if (debugLog)
                                DebugLog($"  Snap match: ghost '{gs.name}' <-> placed '{placed.gameObject.name}/{ps.name}' dist={dist:F4}");
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static void SnapToGrid(GameObject ghost, Piece piece, bool debugLog)
        {
            var pos = ghost.transform.position;
            var rot = ghost.transform.rotation;
            var invRot = Quaternion.Inverse(rot);

            // Convert to local space
            var localPos = invRot * pos;

            // Determine alignment per axis from snap points or default
            float alignX, alignY, alignZ;
            float offsetX, offsetY, offsetZ;

            ComputeAlignment(piece, debugLog, out alignX, out alignY, out alignZ,
                             out offsetX, out offsetY, out offsetZ);

            var preSnap = localPos;

            // Snap each axis
            if (alignX > 0f)
                localPos.x = SnapAxis(localPos.x, alignX, offsetX);
            if (alignY > 0f)
                localPos.y = SnapAxis(localPos.y, alignY, offsetY);
            if (alignZ > 0f)
                localPos.z = SnapAxis(localPos.z, alignZ, offsetZ);

            if (debugLog)
            {
                DebugLog($"  Snap align: X={alignX:F3} Y={alignY:F3} Z={alignZ:F3} | Offset: X={offsetX:F3} Y={offsetY:F3} Z={offsetZ:F3}");
                DebugLog($"  Local pre-snap:  ({preSnap.x:F3}, {preSnap.y:F3}, {preSnap.z:F3})");
                DebugLog($"  Local post-snap: ({localPos.x:F3}, {localPos.y:F3}, {localPos.z:F3})");
                var delta = localPos - preSnap;
                DebugLog($"  Delta: ({delta.x:F3}, {delta.y:F3}, {delta.z:F3}) magnitude={delta.magnitude:F3}");
            }

            // Convert back to world space
            ghost.transform.position = rot * localPos;

            if (debugLog)
                DebugLog($"  Final world pos: ({ghost.transform.position.x:F3}, {ghost.transform.position.y:F3}, {ghost.transform.position.z:F3})");
        }

        private static float SnapAxis(float value, float alignment, float offset)
        {
            value -= offset;
            value = Mathf.Round(value / alignment) * alignment;
            value += offset;
            return value;
        }

        private static void ComputeAlignment(Piece piece, bool debugLog,
            out float alignX, out float alignY, out float alignZ,
            out float offsetX, out float offsetY, out float offsetZ)
        {
            float defaultAlign = _defaultAlignment / 100f;

            // Try to derive alignment from snap points
            List<Transform> snapPoints = new List<Transform>();
            piece.GetSnapPoints(snapPoints);

            if (snapPoints.Count >= 2)
            {
                // Calculate bounding box from snap points in local space
                var invRot = Quaternion.Inverse(piece.transform.rotation);
                var pieceWorldPos = piece.transform.position;

                float minX = float.MaxValue, maxX = float.MinValue;
                float minY = float.MaxValue, maxY = float.MinValue;
                float minZ = float.MaxValue, maxZ = float.MinValue;

                foreach (var sp in snapPoints)
                {
                    var local = invRot * (sp.position - pieceWorldPos);
                    if (local.x < minX) minX = local.x;
                    if (local.x > maxX) maxX = local.x;
                    if (local.y < minY) minY = local.y;
                    if (local.y > maxY) maxY = local.y;
                    if (local.z < minZ) minZ = local.z;
                    if (local.z > maxZ) maxZ = local.z;
                }

                alignX = QuantizeAlignment(maxX - minX, defaultAlign);
                alignY = QuantizeAlignment(maxY - minY, defaultAlign);
                alignZ = QuantizeAlignment(maxZ - minZ, defaultAlign);

                offsetX = maxX;
                offsetY = maxY;
                offsetZ = maxZ;

                if (debugLog)
                {
                    DebugLog($"  Snap bbox: X=[{minX:F3},{maxX:F3}] Y=[{minY:F3},{maxY:F3}] Z=[{minZ:F3},{maxZ:F3}]");
                    DebugLog($"  Snap sizes: X={maxX - minX:F3} Y={maxY - minY:F3} Z={maxZ - minZ:F3}");
                    DebugLog($"  Quantized: X={alignX:F3} Y={alignY:F3} Z={alignZ:F3}");
                }
            }
            else
            {
                // No snap points — use default grid size for all axes
                alignX = defaultAlign;
                alignY = defaultAlign;
                alignZ = defaultAlign;
                offsetX = 0f;
                offsetY = 0f;
                offsetZ = 0f;

                if (debugLog)
                    DebugLog($"  No snap points (count={snapPoints.Count}), using default align: {defaultAlign:F3}");
            }
        }

        private static float QuantizeAlignment(float size, float defaultAlign)
        {
            if (size <= 0.01f) return defaultAlign;
            if (size <= 0.5f) return 0.5f;
            if (size <= 1f) return 1f;
            if (size <= 2f) return 2f;
            return 4f;
        }
    }
}
