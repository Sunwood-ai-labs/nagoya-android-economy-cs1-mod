using System;
using System.Reflection;
using ColossalFramework.UI;
using HarmonyLib;
using UnityEngine;

namespace NagoyaRoadDelivery
{
    internal sealed class NativePopulationCard : IDisposable
    {
        private const string PatchId = "sunwood.nagoya.native-population-card";
        private static NativePopulationCard active;
        private readonly Harmony harmony;
        private UIComponent parent;
        private UIPanel card;
        private UILabel rows;
        private float originalHeight;
        private string text;
        private bool failed;

        internal NativePopulationCard()
        {
            MethodInfo panel = typeof(PopulationInfoViewPanel).GetMethod("UpdatePanel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (panel == null) throw new MissingMethodException("PopulationInfoViewPanel.UpdatePanel");
            MethodInfo callback = typeof(NativePopulationCard).GetMethod("AfterPopulationUpdate", BindingFlags.Static | BindingFlags.NonPublic);
            harmony = new Harmony(PatchId);
            harmony.Patch(panel, null, new HarmonyMethod(callback), null, null);
            active = this;
        }

        internal string Text { set { text = value; } }
        internal bool Visible { get { return card != null && card.isVisible; } }

        private static void AfterPopulationUpdate(PopulationInfoViewPanel __instance)
        {
            NativePopulationCard view = active;
            if (view == null || view.text == null || view.failed) return;
            try { view.Show(__instance.GetComponent<UIComponent>()); }
            catch (Exception error) { view.failed = true; Debug.LogException(error); }
        }

        private void Show(UIComponent container)
        {
            if (container == null || container.width < 175) return;
            if (card == null)
            {
                parent = container;
                originalHeight = container.height;
                card = container.AddUIComponent<UIPanel>();
                card.name = "NagoyaNativePopulation";
                card.width = container.width - 20;
                card.height = 104;
                card.relativePosition = new Vector3(10, Mathf.Max(0, originalHeight - card.height - 17));
                parent.height = originalHeight + card.height + 8;
                UILabel title = card.AddUIComponent<UILabel>();
                title.text = "名古屋 2050　人とアンドロイド";
                title.textScale = 0.9f;
                title.textColor = new Color32(169, 239, 219, 255);
                title.relativePosition = new Vector3(0, 0);
                rows = card.AddUIComponent<UILabel>();
                rows.autoSize = false;
                rows.width = card.width;
                rows.height = 82;
                rows.textScale = 0.8f;
                rows.relativePosition = new Vector3(0, 23);
            }
            if (rows.text != text) rows.text = text;
        }

        public void Dispose()
        {
            if (active == this) active = null;
            harmony.UnpatchAll(PatchId);
            if (parent != null && card != null && Mathf.Abs(parent.height - originalHeight - card.height - 8) < .1f)
                parent.height = originalHeight;
            if (card != null) UnityEngine.Object.Destroy(card.gameObject);
        }
    }
}
