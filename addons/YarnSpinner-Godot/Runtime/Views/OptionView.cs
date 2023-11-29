using System;
using Godot;

namespace YarnSpinnerGodot
{
    public partial class OptionView : Button
    {
        [Export] bool showCharacterName = false;
        [Export] private RichTextLabel label;
        public Action<DialogueOption> OnOptionSelected;
        public MarkupPalette palette;

        DialogueOption _option;

        bool hasSubmittedOptionSelection = false;

        public DialogueOption Option
        {
            get => _option;

            set
            {
                _option = value;

                hasSubmittedOptionSelection = false;

                // When we're given an Option, use its text and update our
                // disabled/enabled state 
                Yarn.Markup.MarkupParseResult line;
                if (showCharacterName)
                {
                    line = value.Line.Text;
                }
                else
                {
                    line = value.Line.TextWithoutCharacterName;
                }

                label.BbcodeEnabled = true;
                if (palette != null)
                {
                    label.Text = $"[center]{LineView.PaletteMarkedUpText(line, palette)}[/center]";
                }
                else
                {
                    label.Text = $"[center]{line.Text}[/center]";
                    ;
                }

                Disabled = !value.IsAvailable;
            }
        }

        public override void _Ready()
        {
            Pressed += InvokeOptionSelected;
        }

        public void InvokeOptionSelected()
        {
            // We only want to invoke this once, because it's an error to
            // submit an option when the Dialogue Runner isn't expecting it. To
            // prevent this, we'll only invoke this if the flag hasn't been cleared already.
            if (hasSubmittedOptionSelection == false)
            {
                OnOptionSelected.Invoke(Option);
                hasSubmittedOptionSelection = true;
            }
        }
    }
}