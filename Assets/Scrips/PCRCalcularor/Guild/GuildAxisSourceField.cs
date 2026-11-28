#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PCRCaculator.Guild
{
    public sealed class GuildAxisSourceField : InputField, IScrollHandler, IInitializePotentialDragHandler
    {
        internal ScrollRect scroll;
        private string measuredText;
        private Vector2 measuredView, clickPosition;
        private PointerEventData panPointer;
        private Camera clickCamera;
        private bool browse, panning, placeCaret;
        private int lastCaret = -1;

        internal void ShowDocument(string value)
        {
            text = value; ResizeDocument();
            scroll.StopMovement(); scroll.verticalNormalizedPosition = 1;
        }

        // The caret and the glyphs belong to the same full-height document, inside the viewport mask.
        private bool ResizeDocument()
        {
            Vector2 size = scroll.viewport.rect.size;
            if (size == measuredView && measuredText == text) return false;
            measuredView = size; measuredText = text;
            var label = textComponent;
            var settings = label.GetGenerationSettings(new Vector2(label.rectTransform.rect.width, 0));
            float lines = label.cachedTextGeneratorForLayout.GetPreferredHeight(text + " ", settings) / label.pixelsPerUnit;
            float inset = -label.rectTransform.sizeDelta.y;
            scroll.content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Mathf.Max(size.y, Mathf.Ceil(lines + inset + 2)));
            SetOffset(scroll.content.anchoredPosition.y); ForceLabelUpdate();
            return true;
        }

        private void SetOffset(float y)
        {
            var content = scroll.content;
            content.anchoredPosition = new Vector2(0, Mathf.Clamp(y, 0, Mathf.Max(0, content.rect.height - scroll.viewport.rect.height)));
        }

        public void OnScroll(PointerEventData e) => scroll.OnScroll(e);

        // ScrollRect accepts left drags only. Keep the original editing event and both raycast cameras intact.
        private PointerEventData ScrollPointer(PointerEventData e)
        {
            if (e.button == PointerEventData.InputButton.Left) return e;
            panPointer ??= new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
            panPointer.position = e.position; panPointer.pressPosition = e.pressPosition; panPointer.delta = e.delta;
            panPointer.pointerPressRaycast = e.pointerPressRaycast; panPointer.pointerCurrentRaycast = e.pointerCurrentRaycast;
            return panPointer;
        }

        public void OnInitializePotentialDrag(PointerEventData e)
        {
            if (browse) scroll.OnInitializePotentialDrag(ScrollPointer(e));
        }

        public override void OnPointerDown(PointerEventData e)
        {
            browse = !isFocused || e.button != PointerEventData.InputButton.Left;
            if (!browse) { scroll.StopMovement(); base.OnPointerDown(e); }
        }

        public override void OnPointerClick(PointerEventData e)
        {
            if (e.button != PointerEventData.InputButton.Left || e.dragging) return;
            if (browse)
            {
                clickPosition = e.position; clickCamera = e.pressEventCamera; placeCaret = true;
            }
            base.OnPointerClick(e);
        }

        public override void OnBeginDrag(PointerEventData e)
        {
            panning = browse;
            if (!panning) { base.OnBeginDrag(e); return; }
            e.eligibleForClick = false;
            scroll.OnBeginDrag(ScrollPointer(e));
        }

        public override void OnDrag(PointerEventData e)
        {
            if (panning) scroll.OnDrag(ScrollPointer(e)); else base.OnDrag(e);
        }

        public override void OnEndDrag(PointerEventData e)
        {
            if (panning) scroll.OnEndDrag(ScrollPointer(e)); else base.OnEndDrag(e);
            panning = false;
        }

        protected override void LateUpdate()
        {
            base.LateUpdate();
            if (scroll == null) return;
            bool resized = ResizeDocument();
            if (!isFocused) { lastCaret = -1; return; }
            if (placeCaret)
            {
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(textComponent.rectTransform, clickPosition, clickCamera, out var point))
                    caretPosition = GetCharacterIndexFromPosition(point);
                placeCaret = false; ForceLabelUpdate();
            }
            int caret = selectionFocusPosition;
            if (panning || (caret == lastCaret && !resized)) return;
            lastCaret = caret; scroll.StopMovement();
            var lines = cachedInputTextGenerator.lines;
            for (int i = lines.Count - 1; i >= 0; --i)
            {
                if (lines[i].startCharIdx > caret) continue;
                float top = -textComponent.rectTransform.offsetMax.y + (lines[0].topY - lines[i].topY) / textComponent.pixelsPerUnit;
                float bottom = top + lines[i].height / textComponent.pixelsPerUnit;
                float offset = scroll.content.anchoredPosition.y, height = scroll.viewport.rect.height;
                if (top < offset) SetOffset(top); else if (bottom > offset + height) SetOffset(bottom - height);
                break;
            }
        }
    }
}
#endif
