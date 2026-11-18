using System.Collections.Generic;
using System.Linq;
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
    /// <remarks>
    /// The report shows each message once. Messages with the same severity and text for several subjects, such as
    /// terrain tiles, become one line with the subject count and the first subject names.
    /// </remarks>
    public sealed class LoogaWorldSetupContext
    {
        private sealed class Message
        {
            public string Severity;
            public string Text;
            public readonly List<string> Subjects = new();
            public bool AllTerrains = true;
        }

        private const int ListedSubjects = 3;
        private readonly List<Component> _owned;
        private readonly List<Message> _messages = new();

        public LoogaWorldController Controller { get; }
        public string OutputFolder => Controller.GeneratedAssetFolder;
        /// <summary>Report lines with grouped subjects, in the order of their first message.</summary>
        public IReadOnlyList<string> Messages => _messages.Select(Format).ToList();

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
            Add("INFO", null, message);
        }

        /// <summary>Adds information about one object. Equal messages of several objects share one report line.</summary>
        public void AddInfo(UnityEngine.Object subject, string message)
        {
            Add("INFO", subject, message);
        }

        public void AddWarning(string message)
        {
            Add("WARNING", null, message);
        }

        /// <summary>Adds a warning about one object. Equal warnings of several objects share one report line.</summary>
        public void AddWarning(UnityEngine.Object subject, string message)
        {
            Add("WARNING", subject, message);
        }

        public void AddError(string message)
        {
            Add("ERROR", null, message);
        }

        private void Add(string severity, UnityEngine.Object subject, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Message message = _messages.Find(item => item.Severity == severity && item.Text == text);
            if (message == null)
            {
                message = new Message { Severity = severity, Text = text };
                _messages.Add(message);
            }
            if (!subject || message.Subjects.Contains(subject.name)) return;
            message.Subjects.Add(subject.name);
            message.AllTerrains &= subject is UnityEngine.Terrain;
        }

        private static string Format(Message message)
        {
            int count = message.Subjects.Count;
            if (count == 0) return message.Severity + ": " + message.Text;
            if (count == 1) return message.Severity + ": " + message.Subjects[0] + ": " + message.Text;
            string names = string.Join(", ", message.Subjects.Take(ListedSubjects));
            if (count > ListedSubjects)
            {
                names += " and " + (count - ListedSubjects) + " more";
            }
            return message.Severity + ": " + count + (message.AllTerrains ? " terrains: " : " objects: ") +
                message.Text + " (" + names + ")";
        }
    }
}
