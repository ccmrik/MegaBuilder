using HarmonyLib;
using System.Collections.Generic;
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

        // Reusable buffers to avoid per-frame GC allocations
        private static readonly Collider[] _overlapBuffer = new Collider[128];
        private static readonly List<Transform> _ghostSnapBuffer = new List<Transform>();
        private static readonly List<Transform> _nearbySnapBuffer = new List<Transform>();
        private static readonly HashSet<int> _checkedPieceIds = new HashSet<int>();

        private static readonly FieldInfo _placementGhostField =
            AccessTools.Field(typeof(Player), "m_placementGhost");

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
            }

            // F6 - Cycle grid size
            if (Input.GetKeyDown(MegaBuilderPlugin.GridSizeCycleKey.Value))
            {
                int idx = System.Array.IndexOf(AlignmentSteps, _defaultAlignment);
                idx = (idx + 1) % AlignmentSteps.Length;
                _defaultAlignment = AlignmentSteps[idx];
                __instance.Message(MessageHud.MessageType.TopLeft,
                    $"Grid size: {_defaultAlignment / 100f}");
            }
        }

        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        [HarmonyPostfix]
        private static void UpdatePlacementGhost_Postfix(Player __instance)
        {
            if (!MegaBuilderPlugin.EnableGridAlignment.Value) return;
            if (!_alignToggled) return;
            if (__instance != Player.m_localPlayer) return;

            var ghost = _placementGhostField?.GetValue(__instance) as GameObject;
            if (ghost == null || !ghost.activeSelf) return;

            var piece = ghost.GetComponent<Piece>();
            if (piece == null) return;

            // Skip grid snapping when vanilla has snapped to an existing piece's snap point.
            // This preserves proper piece connections, door alignment, and corner attachment.
            if (IsSnappedToExistingPiece(ghost, piece)) return;

            SnapToGrid(ghost, piece);
        }

        private static bool IsSnappedToExistingPiece(GameObject ghost, Piece piece)
        {
            const float snapTolerance = 0.1f;

            _ghostSnapBuffer.Clear();
            piece.GetSnapPoints(_ghostSnapBuffer);
            if (_ghostSnapBuffer.Count == 0) return false;

            int pieceMask = LayerMask.GetMask("piece", "piece_nonsolid");
            int count = Physics.OverlapSphereNonAlloc(
                ghost.transform.position, 6f, _overlapBuffer, pieceMask);

            _checkedPieceIds.Clear();

            for (int i = 0; i < count; i++)
            {
                var col = _overlapBuffer[i];
                if (col == null) continue;

                var nearbyPiece = col.GetComponentInParent<Piece>();
                if (nearbyPiece == null) continue;
                if (!_checkedPieceIds.Add(nearbyPiece.GetInstanceID())) continue;
                if (nearbyPiece.gameObject == ghost) continue;

                _nearbySnapBuffer.Clear();
                nearbyPiece.GetSnapPoints(_nearbySnapBuffer);

                foreach (var gs in _ghostSnapBuffer)
                {
                    foreach (var ns in _nearbySnapBuffer)
                    {
                        if (Vector3.Distance(gs.position, ns.position) < snapTolerance)
                            return true;
                    }
                }
            }

            return false;
        }

        private static void SnapToGrid(GameObject ghost, Piece piece)
        {
            var pos = ghost.transform.position;
            var rot = ghost.transform.rotation;
            var invRot = Quaternion.Inverse(rot);

            // Convert to local space
            var localPos = invRot * pos;

            // Determine alignment per axis from snap points or default
            float alignX, alignY, alignZ;
            float offsetX, offsetY, offsetZ;

            ComputeAlignment(piece, out alignX, out alignY, out alignZ,
                             out offsetX, out offsetY, out offsetZ);

            // Snap each axis
            if (alignX > 0f)
                localPos.x = SnapAxis(localPos.x, alignX, offsetX);
            if (alignY > 0f)
                localPos.y = SnapAxis(localPos.y, alignY, offsetY);
            if (alignZ > 0f)
                localPos.z = SnapAxis(localPos.z, alignZ, offsetZ);

            // Convert back to world space
            ghost.transform.position = rot * localPos;
        }

        private static float SnapAxis(float value, float alignment, float offset)
        {
            value -= offset;
            value = Mathf.Round(value / alignment) * alignment;
            value += offset;
            return value;
        }

        private static void ComputeAlignment(Piece piece,
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
