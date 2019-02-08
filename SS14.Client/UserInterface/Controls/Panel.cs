﻿using SS14.Client.Graphics.Drawing;
using SS14.Shared.Maths;

namespace SS14.Client.UserInterface.Controls
{
    [ControlWrap(typeof(Godot.Panel))]
    public class Panel : Control
    {
        public Panel(string name) : base(name)
        {
        }

        public Panel() : base()
        {
        }

        internal Panel(Godot.Panel panel) : base(panel)
        {
        }

        private protected override Godot.Control SpawnSceneControl()
        {
            return new Godot.Panel();
        }

        private StyleBox _panelOverride;

        public StyleBox PanelOverride
        {
            get => _panelOverride ?? GetStyleBoxOverride("panel");
            set => SetStyleBoxOverride("panel", _panelOverride = value);
        }

        protected internal override void Draw(DrawingHandleScreen handle)
        {
            base.Draw(handle);

            if (!GameController.OnGodot)
            {
                var panel = _panelOverride ?? UserInterfaceManager.Theme.PanelPanel;
                panel.Draw(handle, UIBox2.FromDimensions(Vector2.Zero, Size));
            }
        }
    }
}
