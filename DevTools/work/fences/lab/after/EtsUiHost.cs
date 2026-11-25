using System;
using System.Collections.Generic;
using UnityEngine;

namespace SomniumSpace.Worlds.SomniumEuroTruck
{
    /// <summary>
    /// Where game screens are built: null = on a canvas of their own (every screen the world has ever built), a
    /// frame = inside that frame, which is how a page of the "ETS 2" tablet app holds a game screen (owner E;
    /// [tablet §4.4], PLAN.md C3, §7.E).
    ///
    /// Why a scope and not a parameter: EtsGameUi.BuildScreen and EtsFrontEnd's screens create their canvas deep
    /// inside (EtsGameUi.cs:663 today), and D routes that one call through EtsUiSurface.Create. A page then builds
    /// any game screen unchanged with
    ///     using (EtsUiHost.Into(Root)) { EtsGameUi.BuildScreen(...); }
    /// and every coordinate of the game's layout file lands where it does on a canvas of its own, because the
    /// page's Root is the same 1440 x 900 frame (/def/ui.sii ref_res_x/ref_res_y).
    ///
    /// Target is null unless a scope is open, so with nothing calling Into - the whole game today, and every build
    /// while EtsFeatures.NewFlow is false - EtsUiSurface.Create makes exactly the canvas CreateCanvas always made.
    /// Scopes nest: each restores the target it found. A destroyed target reads as null (Unity's null test), so a
    /// scope left open by an exception can only ever fall back to a canvas, never build into a dead frame.
    /// </summary>
    public static class EtsUiHost
    {
        private static RectTransform _target;

        /// <summary>The frame screens are built into, or null for a canvas of their own.</summary>
        public static RectTransform Target => _target != null ? _target : null;

        /// <summary>Restores the previous target when disposed. A default Scope (never opened) restores nothing.</summary>
        public readonly struct Scope : IDisposable
        {
            private readonly RectTransform _previous;
            private readonly bool _opened;

            internal Scope(RectTransform previous)
            {
                _previous = previous;
                _opened = true;
            }

            public void Dispose()
            {
                if (_opened)
                {
                    _target = _previous;
                }
            }
        }

        /// <summary>using (EtsUiHost.Into(Root)) { EtsGameUi.BuildScreen(...); } builds the screen inside Root.</summary>
        public static Scope Into(RectTransform frame)
        {
            var scope = new Scope(_target);
            _target = frame;
            return scope;
        }
    }

    /// <summary>
    /// A screen surface's sorting order inside a host frame, where there is no Canvas to sort by (owner E;
    /// [tablet §4.4]). On a canvas of its own a screen is drawn by Canvas.sortingOrder (the loading screen 500
    /// over the desktop 400, EtsFrontEnd); inside one frame UGUI draws siblings in hierarchy order, so the same
    /// numbers are kept here and the siblings are put in ascending Order. Children without one count as 0 and
    /// keep their relative order (the sort is stable).
    /// </summary>
    public sealed class EtsUiOrder : MonoBehaviour
    {
        public int Order;

        private static readonly List<Transform> Children = new List<Transform>();
        private static readonly List<int> Orders = new List<int>();

        /// <summary>Puts the host's children in ascending Order. Runs when a surface is made, never per frame.</summary>
        public static void Sort(RectTransform host)
        {
            if (host == null)
            {
                return;
            }

            Children.Clear();
            Orders.Clear();

            for (int i = 0; i < host.childCount; i++)
            {
                Transform child = host.GetChild(i);
                var order = child.GetComponent<EtsUiOrder>();
                Children.Add(child);
                Orders.Add(order != null ? order.Order : 0);
            }

            // Insertion sort: stable, and a host holds a handful of surfaces.
            for (int i = 1; i < Children.Count; i++)
            {
                Transform t = Children[i];
                int o = Orders[i];
                int j = i - 1;

                while (j >= 0 && Orders[j] > o)
                {
                    Children[j + 1] = Children[j];
                    Orders[j + 1] = Orders[j];
                    j--;
                }

                Children[j + 1] = t;
                Orders[j + 1] = o;
            }

            for (int i = 0; i < Children.Count; i++)
            {
                if (Children[i].GetSiblingIndex() != i)
                {
                    Children[i].SetSiblingIndex(i);
                }
            }

            Children.Clear();
            Orders.Clear();
        }
    }

    /// <summary>
    /// A surface for one screen in the 1440 x 900 frame (owner E; [tablet §4.4], PLAN.md §7.E): its own canvas
    /// when EtsUiHost.Target is null - EtsGameUi.CreateCanvas, byte for byte the canvas every screen gets today
    /// (overlay with the 1440 x 900 scaler on a monitor, EtsVrPanel in a headset) - or, inside a host, a plain
    /// rectangle over the host's whole frame with no Canvas, no scaler and no EtsVrPanel, ordered among its
    /// siblings by EtsUiOrder. A nested Canvas would need its own raycaster to take clicks ([tablet §2.5]:
    /// UGUI registers graphics with their nearest Canvas), which is why a hosted surface has none.
    /// </summary>
    public static class EtsUiSurface
    {
        /// <summary>Canvas or host rect. A non-interactive hosted surface lets the pointer through (CanvasGroup).</summary>
        public static GameObject Create(string name, int sortingOrder, bool interactive = true)
        {
            RectTransform host = EtsUiHost.Target;

            if (host == null)
            {
                return EtsGameUi.CreateCanvas(name, sortingOrder, interactive).gameObject;
            }

            RectTransform surface = EtsGameUi.Place(host, name, 0f, EtsGameUi.ReferenceWidth, 0f, EtsGameUi.ReferenceHeight);
            surface.gameObject.layer = host.gameObject.layer;
            surface.gameObject.AddComponent<EtsUiOrder>().Order = sortingOrder;

            if (!interactive)
            {
                var group = surface.gameObject.AddComponent<CanvasGroup>();
                group.interactable = false;
                group.blocksRaycasts = false;
            }

            EtsUiOrder.Sort(host);
            return surface.gameObject;
        }
    }
}
