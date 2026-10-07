#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BAAA
{
    // pieces tab. body outline in the middle, every piece next to where its worn, click one to take it off
    public partial class BaaaWindow
    {
        private const float TileSize = 56f;
        private const float TileGap = 8f;
        private const float TileCaption = 14f;
        private const float BlockHeader = 16f;

        private static readonly BodyRegion[] LeftSide = { BodyRegion.Head, BodyRegion.Arms, BodyRegion.Hands, BodyRegion.Legs };
        private static readonly BodyRegion[] RightSide = { BodyRegion.Neck, BodyRegion.Torso, BodyRegion.Hips, BodyRegion.Tail, BodyRegion.Feet };
        private static readonly Color ColFigureBase = new Color(0.16f, 0.15f, 0.12f, 1f);
        private static readonly Color ColShade = new Color(0.051f, 0.047f, 0.039f, 0.72f);

        private List<Piece> _pieces;
        private float _piecesWidth;

        private GUIStyle _styleCaption;
        private GUIStyle _styleCaptionDim;
        private GUIStyle _styleDropped;
        private GUIStyle _styleInitials;

        private struct Block
        {
            public BodyRegion Region;
            public bool Left;
            public bool HasAnchor;
            public Rect Rect;
            public Vector2 Anchor;
            public List<Piece> Pieces;
        }

        private void DrawPieces()
        {
            PageHeader(T("Pieces"), T("Wear what you *want*"), T("Click a piece to take it off. Click again to put it back. Pieces you take off aren't uploaded, so your avatar is more optimized. Pieces you keep are uploaded with their toggle."));

            Outfit o = Selected;
            if (o.Copy != null && o.Copy.Kind != CopyKind.Decimated)
            {
                Notice(Tone.Neutral, null, T("This is the {0} version. Go back to the original to change its pieces.", KindName(o.Copy.Kind)));
                return;
            }
            if (!AssistantScope.VrcFuryInstalled)
            {
                Notice(Tone.Warn, null, T("VRCFury isn't installed, so this outfit's pieces can't be read. The Check tab shows how to fix that."));
                return;
            }
            if (_pieces == null)
            {
                GUILayout.Label(T("Looking at the pieces..."), _styleBody);
                return;
            }
            if (_pieces.Count == 0)
            {
                GUILayout.Label(T("This outfit has no pieces that switch on and off."), _styleBody);
                return;
            }

            bool canEdit = OutfitPieces.CanEdit(o);
            if (!canEdit)
            {
                Notice(Tone.Neutral, null, o.Place == OutfitPlace.PackFile
                    ? T("This is the pack's own prefab. Put the outfit in your scene to pick its pieces.")
                    : T("Pieces are picked on outfits in your scene, not on the pack's own prefab."));
            }

            // the layout pass hands out a placeholder rect, so the height comes from
            // the width the canvas had last time it was drawn.
            GUILayout.Space(12);
            float width = _piecesWidth > 0f ? _piecesWidth : position.width - 46f;
            float height;
            LayoutPieces(new Rect(0f, 0f, width, 0f), out height);
            Rect canvas = GUILayoutUtility.GetRect(width, height, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint && Mathf.Abs(canvas.width - _piecesWidth) > 1f)
            {
                _piecesWidth = canvas.width;
                Repaint();
            }

            Piece hover = null;
            if (canvas.width > 2f)
            {
                List<Block> blocks = LayoutPieces(canvas, out height);
                hover = HoveredPiece(blocks);
                DrawFigure(FigureRect(canvas), hover != null ? hover.Region : (BodyRegion?)null, _pieces.Exists(p => p.Region == BodyRegion.Tail));
                foreach (Block b in blocks) DrawBlock(b, canEdit, hover);
            }

            GUILayout.Space(10);
            Rect info = GUILayoutUtility.GetRect(10f, 36f, GUILayout.ExpandWidth(true));
            GUI.Label(info, PieceInfo(hover, canEdit), _styleBody);

            GUILayout.Space(6);
            if (SecondaryButton(T("Put everything back"), canEdit && _pieces.Exists(p => p.Dropped)))
            {
                OutfitPieces.RestoreAll(o, _pieces);
                AfterPieceChange();
            }
        }

        private void AfterPieceChange()
        {
            _pieces = null;
            _extremeNotes = null;
            _features = null;
            _scan = null;
            MarkDirty();
            GUIUtility.ExitGUI();
        }

        // -------------------------------------------------------------------
        // Layout: the figure in the middle, regions on either side, extras below
        // -------------------------------------------------------------------

        // Unity's humanoid figure from the Avatar mapping view. Its images are
        // 180x378 and the body fills 0.117 to 0.883 of their width.
        private const float FigureAspect = 378f / 180f;
        private const float BodyLeft = 0.117f;
        private const float BodyRight = 0.883f;

        private static Rect FigureRect(Rect canvas)
        {
            float w = Mathf.Clamp(canvas.width * 0.30f, 150f, 190f);
            return new Rect(canvas.center.x - w * 0.5f, canvas.y, w, w * FigureAspect);
        }

        private List<Block> LayoutPieces(Rect canvas, out float height)
        {
            var blocks = new List<Block>();
            Rect figure = FigureRect(canvas);
            float bodyLeft = figure.x + BodyLeft * figure.width;
            float bodyRight = figure.x + BodyRight * figure.width;
            const float gap = 22f;
            float bottom = figure.yMax;
            bottom = Mathf.Max(bottom, PlaceSide(LeftSide, true, canvas.x, bodyLeft - gap - canvas.x, canvas.y, figure, blocks));
            bottom = Mathf.Max(bottom, PlaceSide(RightSide, false, bodyRight + gap, canvas.xMax - bodyRight - gap, canvas.y, figure, blocks));

            List<Piece> extras = PiecesIn(BodyRegion.Extras);
            if (extras.Count > 0)
            {
                float h = BlockHeight(extras.Count, canvas.width);
                blocks.Add(new Block { Region = BodyRegion.Extras, Rect = new Rect(canvas.x, bottom + 14f, canvas.width, h), Pieces = extras });
                bottom += 14f + h;
            }
            height = bottom - canvas.y + 6f;
            return blocks;
        }

        private float PlaceSide(BodyRegion[] regions, bool left, float x, float width, float top, Rect figure, List<Block> blocks)
        {
            float y = top;
            foreach (BodyRegion region in regions)
            {
                List<Piece> pieces = PiecesIn(region);
                if (pieces.Count == 0) continue;
                Vector2 anchor = Anchor(region, left, figure);
                float h = BlockHeight(pieces.Count, width);
                float blockTop = Mathf.Max(y, anchor.y - 8f);
                blocks.Add(new Block { Region = region, Left = left, HasAnchor = true, Rect = new Rect(x, blockTop, width, h), Anchor = anchor, Pieces = pieces });
                y = blockTop + h + 10f;
            }
            return y;
        }

        private List<Piece> PiecesIn(BodyRegion region)
        {
            var list = new List<Piece>();
            foreach (Piece p in _pieces) if (p.Region == region) list.Add(p);
            return list;
        }

        private static int PerRow(float width)
        {
            return Mathf.Max(1, Mathf.FloorToInt((width + TileGap) / (TileSize + TileGap)));
        }

        private static float BlockHeight(int count, float width)
        {
            int perRow = PerRow(width);
            int rows = (count + perRow - 1) / perRow;
            return BlockHeader + rows * (TileSize + TileCaption + TileGap);
        }

        // tiles on the left sit against the figure, so they line up from the right.
        private static Rect TileRect(Block b, int index)
        {
            int perRow = PerRow(b.Rect.width);
            int row = index / perRow;
            int col = index % perRow;
            float x = b.Left
                ? b.Rect.xMax - (col + 1) * TileSize - col * TileGap
                : b.Rect.x + col * (TileSize + TileGap);
            float y = b.Rect.y + BlockHeader + row * (TileSize + TileCaption + TileGap);
            return new Rect(x, y, TileSize, TileSize);
        }

        // points on Unity's figure, measured from its body part images. The figure
        // faces you, so its right arm is on the left of the screen
        private static Vector2 Anchor(BodyRegion region, bool left, Rect f)
        {
            switch (region)
            {
                case BodyRegion.Head: return At(f, 0.497f, 0.096f);
                case BodyRegion.Neck: return At(f, 0.497f, 0.165f);
                case BodyRegion.Torso: return At(f, 0.497f, 0.28f);
                case BodyRegion.Hips: return At(f, 0.497f, 0.47f);
                case BodyRegion.Tail: return At(f, 0.80f, 0.66f);
                case BodyRegion.Arms: return At(f, left ? 0.282f : 0.712f, 0.323f);
                case BodyRegion.Hands: return At(f, left ? 0.158f : 0.836f, 0.525f);
                case BodyRegion.Legs: return At(f, left ? 0.395f : 0.600f, 0.68f);
                default: return At(f, left ? 0.38f : 0.615f, 0.945f);
            }
        }

        private static Vector2 At(Rect f, float u, float v)
        {
            return new Vector2(f.x + u * f.width, f.y + v * f.height);
        }

        private Piece HoveredPiece(List<Block> blocks)
        {
            Vector2 mouse = Event.current.mousePosition;
            foreach (Block b in blocks)
                for (int i = 0; i < b.Pieces.Count; i++)
                    if (TileRect(b, i).Contains(mouse)) return b.Pieces[i];
            return null;
        }

        // -------------------------------------------------------------------
        // Drawing
        // -------------------------------------------------------------------

        private static readonly string[] FigureParts = { "Head", "Torso", "LeftArm", "RightArm", "LeftFingers", "RightFingers", "LeftLeg", "RightLeg" };
        private static Texture _figureSilhouette;
        private static Dictionary<string, Texture> _figureParts;

        // the same images Unity's Avatar mapping view draws, built into the editor
        private static bool LoadFigure()
        {
            if (_figureParts != null) return _figureSilhouette != null;
            _figureParts = new Dictionary<string, Texture>();
            _figureSilhouette = BuiltIn("BodySilhouette");
            foreach (string part in FigureParts)
            {
                Texture t = BuiltIn(part);
                if (t != null) _figureParts[part] = t;
            }
            if (_figureParts.Count < FigureParts.Length) _figureSilhouette = null;
            return _figureSilhouette != null;
        }

        private static Texture BuiltIn(string name)
        {
            try { return EditorGUIUtility.FindTexture("AvatarInspector/" + name); }
            catch (Exception) { return null; }
        }

        private static string[] PartsFor(BodyRegion? region)
        {
            switch (region)
            {
                case BodyRegion.Head:
                case BodyRegion.Neck: return new[] { "Head" };
                case BodyRegion.Torso:
                case BodyRegion.Hips: return new[] { "Torso" };
                case BodyRegion.Arms: return new[] { "LeftArm", "RightArm" };
                case BodyRegion.Hands: return new[] { "LeftFingers", "RightFingers" };
                case BodyRegion.Legs:
                case BodyRegion.Feet: return new[] { "LeftLeg", "RightLeg" };
                default: return new string[0];
            }
        }

        // Unity's rig figure in yellow: a dark base, each body part tinted on top,
        // and the part being pointed at drawn over and over until it glows.
        private void DrawFigure(Rect f, BodyRegion? lit, bool tail)
        {
            if (Event.current.type != EventType.Repaint) return;
            // Unity's figure has no tail, so one comes out from behind the hips
            if (tail)
            {
                bool tailLit = lit == BodyRegion.Tail;
                Handles.color = tailLit ? ColYellow : new Color(ColYellow.r, ColYellow.g, ColYellow.b, 0.45f);
                Handles.DrawAAPolyLine(f.width * (tailLit ? 0.05f : 0.04f),
                    Point(f, 0.52f, 0.47f), Point(f, 0.64f, 0.51f), Point(f, 0.74f, 0.57f), Point(f, 0.80f, 0.66f));
            }
            if (!LoadFigure()) return;

            Color before = GUI.color;
            GUI.color = ColFigureBase;
            GUI.DrawTexture(f, _figureSilhouette, ScaleMode.StretchToFill, true);
            string[] litParts = PartsFor(lit);
            GUI.color = ColYellow;
            foreach (string part in FigureParts)
            {
                int passes = Array.IndexOf(litParts, part) >= 0 ? 6 : 1;
                for (int i = 0; i < passes; i++) GUI.DrawTexture(f, _figureParts[part], ScaleMode.StretchToFill, true);
            }
            GUI.color = before;
        }

        private static Vector3 Point(Rect f, float u, float v)
        {
            return new Vector3(f.x + u * f.width, f.y + v * f.height, 0f);
        }

        private void DrawBlock(Block b, bool canEdit, Piece hover)
        {
            bool lit = hover != null && hover.Region == b.Region;
            if (b.HasAnchor && Event.current.type == EventType.Repaint)
            {
                var from = new Vector3(b.Left ? b.Rect.xMax + 4f : b.Rect.x - 4f, b.Rect.y + 7f, 0f);
                var to = new Vector3(b.Anchor.x, b.Anchor.y, 0f);
                Handles.color = lit ? ColYellow : ColLine;
                Handles.DrawAAPolyLine(1.5f, from, to);
                // A ring with a dot, like the bone markers in Unity's mapping view.
                Handles.color = lit ? Color.white : ColYellow;
                Handles.DrawWireDisc(to, Vector3.forward, 4.5f, 1.5f);
                Handles.DrawSolidDisc(to, Vector3.forward, 1.8f);
            }
            var header = new GUIStyle(_styleTileLabel)
            {
                alignment = b.Left ? TextAnchor.UpperRight : TextAnchor.UpperLeft,
                normal = { textColor = lit ? ColYellow : ColDim },
            };
            GUI.Label(new Rect(b.Rect.x, b.Rect.y, b.Rect.width, BlockHeader), RegionName(b.Region).ToUpperInvariant(), header);
            for (int i = 0; i < b.Pieces.Count; i++) DrawTile(TileRect(b, i), b.Pieces[i], canEdit, hover == b.Pieces[i]);
        }

        private void DrawTile(Rect r, Piece p, bool canEdit, bool hover)
        {
            DrawRect(r, ColSurface);
            Color edge = hover && canEdit ? ColYellow : p.Dropped ? ColRedSoft : ColLine;
            DrawRect(new Rect(r.x, r.y, r.width, 1f), edge);
            DrawRect(new Rect(r.x, r.yMax - 1f, r.width, 1f), edge);
            DrawRect(new Rect(r.x, r.y, 1f, r.height), edge);
            DrawRect(new Rect(r.xMax - 1f, r.y, 1f, r.height), edge);

            Texture picture = p.Icon != null ? p.Icon : Preview(p);
            var inner = new Rect(r.x + 5f, r.y + 5f, r.width - 10f, r.height - 10f);
            if (Event.current.type == EventType.Repaint)
            {
                if (picture != null) GUI.DrawTexture(inner, picture, ScaleMode.ScaleToFit, true);
                else GUI.Label(inner, Initials(p.Name), _styleInitials);
            }
            if (p.Dropped)
            {
                DrawRect(r, ColShade);
                GUI.Label(r, T("Off").ToUpperInvariant(), _styleDropped);
            }
            GUI.Label(new Rect(r.x - 4f, r.yMax + 1f, r.width + 8f, TileCaption), p.Name, p.Dropped ? _styleCaptionDim : _styleCaption);

            if (hover) Repaint();
            if (canEdit && GUI.Button(r, GUIContent.none, GUIStyle.none))
            {
                if (p.Dropped) OutfitPieces.Restore(Selected, p);
                else OutfitPieces.Drop(Selected, p);
                AfterPieceChange();
            }
        }

        // A rendered picture of the pieces mesh, for pieces without a menu icon
        private Texture Preview(Piece p)
        {
            if (p.PreviewMesh == null) return null;
            Texture2D preview = AssetPreview.GetAssetPreview(p.PreviewMesh);
            if (preview == null && AssetPreview.IsLoadingAssetPreview(p.PreviewMesh.GetInstanceID())) Repaint();
            return preview;
        }

        private static string Initials(string name)
        {
            string letters = "";
            foreach (string word in name.Split(' '))
                if (word.Length > 0 && letters.Length < 2) letters += char.ToUpperInvariant(word[0]);
            return letters;
        }

        private string PieceInfo(Piece hover, bool canEdit)
        {
            if (hover != null)
            {
                if (hover.Dropped)
                    return T("{0} is off and won't upload.", hover.Name) + (canEdit ? Lang.Space + T("Click to put it back.") : "");
                return T("{0} costs {1}.", hover.Name, CostText(hover.Cost)) + (canEdit ? Lang.Space + T("Click to take it off.") : "");
            }
            int dropped = 0;
            var saved = new PieceCost();
            foreach (Piece p in _pieces)
            {
                if (!p.Dropped) continue;
                dropped++;
                saved.SkinnedMeshes += p.Cost.SkinnedMeshes;
                saved.Meshes += p.Cost.Meshes;
                saved.Triangles += p.Cost.Triangles;
                saved.MaterialSlots += p.Cost.MaterialSlots;
                saved.PhysBones += p.Cost.PhysBones;
            }
            string text = T("{0}, {1} off.", Plural(_pieces.Count, "{0} piece", "{0} pieces"), dropped);
            if (dropped > 0) return text + Lang.Space + T("Taking them off saves {0}.", CostText(saved));
            return canEdit ? text + Lang.Space + T("Point at a piece to see what it costs.") : text;
        }

        private static string CostText(PieceCost c)
        {
            var parts = new List<string>();
            if (c.SkinnedMeshes > 0) parts.Add(Plural(c.SkinnedMeshes, "{0} skinned mesh", "{0} skinned meshes"));
            if (c.Meshes > 0) parts.Add(Plural(c.Meshes, "{0} mesh", "{0} meshes"));
            parts.Add(Plural(c.Triangles, "{0} triangle", "{0} triangles"));
            if (c.MaterialSlots > 0) parts.Add(Plural(c.MaterialSlots, "{0} material slot", "{0} material slots"));
            if (c.PhysBones > 0) parts.Add(Plural(c.PhysBones, "{0} physbone", "{0} physbones"));
            if (parts.Count == 1) return parts[0];
            return T("{0} and {1}", string.Join(Lang.List, parts.GetRange(0, parts.Count - 1).ToArray()), parts[parts.Count - 1]);
        }

        private static string RegionName(BodyRegion region)
        {
            switch (region)
            {
                case BodyRegion.Head: return T("Head");
                case BodyRegion.Neck: return T("Neck");
                case BodyRegion.Torso: return T("Torso");
                case BodyRegion.Hips: return T("Hips");
                case BodyRegion.Tail: return T("Tail");
                case BodyRegion.Arms: return T("Arms");
                case BodyRegion.Hands: return T("Hands");
                case BodyRegion.Legs: return T("Legs");
                case BodyRegion.Feet: return T("Feet");
                default: return T("Extras");
            }
        }
    }
}
#endif
