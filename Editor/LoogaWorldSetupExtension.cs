using System.Collections.Generic;
using LoogaSoft.Terrain.Instances;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Editor
{
    /// <summary>Lets optional packages join world setup without adding core dependencies.</summary>
    public abstract class LoogaWorldSetupExtension
    {
        public virtual int Order => 0;

        public virtual bool HandlesSurface(UnityEngine.Terrain terrain)
        {
            return false;
        }

        public virtual bool TryCreateSurface(UnityEngine.Terrain terrain, LoogaWorldSetupContext context,
            out Material surface, out string diagnostic)
        {
            surface = null;
            diagnostic = null;
            return false;
        }

        public virtual void ConfigureTerrain(UnityEngine.Terrain terrain, TerrainRenderer renderer,
            LoogaWorldSetupContext context)
        {
        }

        public virtual void ConfigureWorld(IReadOnlyList<TerrainRenderer> renderers,
            IReadOnlyList<TerrainVegetationProvider> vegetation, LoogaWorldSetupContext context)
        {
        }

        public virtual void ValidateWorld(LoogaWorldSetupContext context)
        {
        }
    }

    /// <summary>Provides reversible setup operations and one combined report.</summary>
    public sealed class LoogaWorldSetupContext
    {
        private readonly List<Component> _owned;
        private readonly List<string> _messages = new();

        public LoogaWorldController Controller { get; }
        public string OutputFolder => Controller.GeneratedAssetFolder;
        public IReadOnlyList<string> Messages => _messages;

        internal LoogaWorldSetupContext(LoogaWorldController controller, List<Component> owned)
        {
            Controller = controller;
            _owned = owned;
        }

        public T GetOrAddOwned<T>(GameObject owner) where T : Component
        {
            T component = owner.GetComponent<T>();
            if (component) return component;
            component = Undo.AddComponent<T>(owner);
            _owned.Add(component);
            return component;
        }

        public bool IsOwned(Component component)
        {
            return component && _owned.Contains(component);
        }

        public void AddInfo(string message)
        {
            if (!string.IsNullOrEmpty(message)) _messages.Add("INFO: " + message);
        }

        public void AddWarning(string message)
        {
            if (!string.IsNullOrEmpty(message)) _messages.Add("WARNING: " + message);
        }

        public void AddError(string message)
        {
            if (!string.IsNullOrEmpty(message)) _messages.Add("ERROR: " + message);
        }
    }
}
