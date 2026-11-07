#!/usr/bin/env python3
"""Regenerate the S12 Haptics tab cards in UI/SettingsControl.xaml.

Rewrites everything between the S12-GEN-START / S12-GEN-END markers: a live
output card (scope + Test all) followed by one card per S12 firmware channel,
in slot order. Element names follow S12{Channel}{Field}; every handler is
tag-dispatched (Tag="abs", Tag="abs:frequency"), so UI/SettingsControl.PedalHaptics.cs
needs no per-channel code.

    python3 tools/rebuild_s12_cards.py [--check]

--check exits non-zero if the file would change (no writes).
"""
import argparse
import os
import sys

XAML = os.path.join(os.path.dirname(__file__), "..", "UI", "SettingsControl.xaml")
START = "<!-- S12-GEN-START -->"
END = "<!-- S12-GEN-END -->"

# (tag, element prefix, title resource key) in slot order 0..8.
CHANNELS = [
    ("tc", "Tc", "Section_TractionControl"),
    ("abs", "Abs", "Section_Abs"),
    ("lockup", "Lockup", "Section_Lockup"),
    ("threshold", "Threshold", "Section_Threshold"),
    ("engine", "Engine", "Section_EngineContinuous"),
    ("bite", "Bite", "Section_BitePoint"),
    ("gear", "Gear", "Section_MBoosterGearShift"),
    ("slip", "Slip", "Section_WheelSpin"),
    ("road", "Road", "Section_RoadTexture"),
]

FX_BUTTON = ('<Button Grid.Column="{col}" Tag="{tag}" Click="S12Fx_Click" '
             'ToolTip="{{x:Static res:Strings.Tooltip_EditFormula}}" Width="34" Height="22" '
             'Padding="2" Margin="4,0,0,0" Background="Transparent" BorderThickness="0" '
             'Cursor="Hand" VerticalAlignment="Center"><TextBlock Text="𝑓(𝑥)" '
             'FontFamily="Cambria Math, Segoe UI" FontSize="14" HorizontalAlignment="Center" '
             'VerticalAlignment="Center" Foreground="{{DynamicResource TextDimBrush}}"/></Button>')

CALC = ('<TextBlock Grid.Column="{col}" x:Name="{name}" Visibility="Collapsed" '
        'Foreground="{{StaticResource CyanBrush}}" FontFamily="{{StaticResource FontMono}}" '
        'FontSize="13" Width="58" TextAlignment="Right" VerticalAlignment="Center"/>')

FORMULA_TEXT = ('<TextBlock x:Name="{name}" Visibility="Collapsed" Foreground="{{StaticResource CyanBrush}}" '
                'FontFamily="{{StaticResource FontMono}}" FontSize="11" TextTrimming="CharacterEllipsis" '
                'Margin="0,2,0,0"/>')

FOUR_COLS = ('<Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/>'
             '<ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>')
TWO_COLS = '<Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>'


def slider_param(p, tag, field, label_key, slider_name, value_name, minimum, maximum, handler):
    """A slider-or-formula parameter: label, value box, calc readout, f(x), slider, formula line."""
    return [
        '<Grid Margin="0,6,0,0">',
        "    " + FOUR_COLS,
        f'    <TextBlock Grid.Column="0" Text="{{x:Static res:Strings.{label_key}}}" Style="{{StaticResource LfeLabel}}" VerticalAlignment="Center"/>',
        f'    <TextBox Grid.Column="1" x:Name="{value_name}" Width="58" Style="{{StaticResource SliderValueEditBox}}" '
        f'Tag="{{Binding ElementName={slider_name}}}" KeyDown="SliderValueBox_KeyDown" LostFocus="SliderValueBox_LostFocus"/>',
        "    " + CALC.format(col=2, name=f"{p}{field}Calc"),
        "    " + FX_BUTTON.format(col=3, tag=f"{tag}:{field.lower()}"),
        "</Grid>",
        f'<Slider x:Name="{slider_name}" Tag="{tag}" Style="{{StaticResource SettingSlider}}" Minimum="{minimum}" '
        f'Maximum="{maximum}" TickFrequency="1" Margin="0,2,0,0" ValueChanged="{handler}"/>',
        FORMULA_TEXT.format(name=f"{p}{field}FormulaText"),
    ]


def card(index, tag, cap, title_key):
    p = "S12" + cap
    row, col = divmod(index + 1, 2)   # cell 0 is the output card
    margin = "0,0,7,14" if col == 0 else "7,0,0,14"
    road = tag == "road"
    lines = [
        f'<ctrl:SectionCard Grid.Row="{row}" Grid.Column="{col}" VerticalAlignment="Top" '
        f'Title="{{x:Static res:Strings.{title_key}}}" Icon="{{StaticResource IconShift}}" Margin="{margin}">',
        "    <ctrl:SectionCard.Header>",
        f'        <Button Content="{{x:Static res:Strings.Label_Test}}" Tag="{tag}" Padding="10,2" Click="S12Test_Click"/>',
        "    </ctrl:SectionCard.Header>",
        "    <StackPanel>",
    ]
    body = [
        f'<ctrl:OffOnToggle x:Name="{p}Enable" Tag="{tag}" Content="{{x:Static res:Strings.Label_Enable}}" FontSize="14.5" '
        'FontWeight="Normal" HorizontalAlignment="Left" Margin="0,0,0,8" Checked="S12Enable_Changed" Unchecked="S12Enable_Changed"/>',
        '<Grid Margin="0,0,0,8">',
        '    <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>',
        '    <TextBlock Grid.Column="0" Text="{x:Static res:Strings.Label_Pedals}" Style="{StaticResource LfeLabel}" VerticalAlignment="Center"/>',
        '    <WrapPanel Grid.Column="1" VerticalAlignment="Center">',
    ]
    for pedal, key in (("throttle", "Option_Throttle"), ("brake", "Option_Brake"), ("clutch", "Option_Clutch")):
        body.append(
            f'        <ctrl:OffOnToggle x:Name="{p}{pedal.capitalize()}" Tag="{tag}:{pedal}" '
            f'Content="{{x:Static res:Strings.{key}}}" FontWeight="Normal" Margin="0,0,12,0" '
            'Checked="S12Pedal_Changed" Unchecked="S12Pedal_Changed"/>')
    body += [
        "    </WrapPanel>",
        "</Grid>",
        '<Grid Margin="0,2,0,0">',
        '    <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/>'
        '<ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>',
        '    <TextBlock Grid.Column="0" Text="{x:Static res:Strings.SliderLabel_Trigger}" Style="{StaticResource LfeLabel}" VerticalAlignment="Center"/>',
        f'    <ctrl:SegmentedControl Grid.Column="1" x:Name="{p}TriggerMode" Tag="{tag}" HorizontalAlignment="Left" '
        'SelectionChanged="S12TriggerMode_Changed"><ListBoxItem Content="{x:Static res:Strings.TriggerMode_Level}"/>'
        '<ListBoxItem Content="{x:Static res:Strings.TriggerMode_OnChange}"/></ctrl:SegmentedControl>',
        "    " + CALC.format(col=2, name=f"{p}TriggerCalc"),
        "    " + FX_BUTTON.format(col=3, tag=f"{tag}:trigger"),
        "</Grid>",
        f'<TextBlock x:Name="{p}TriggerText" Margin="0,4,0,0" Foreground="{{StaticResource TextDimBrush}}" FontSize="11" TextTrimming="CharacterEllipsis"/>',
    ]
    if road:
        # Position is formula-only: a constant position settles to silence.
        body += [
            '<Grid Margin="0,6,0,0">',
            "    " + FOUR_COLS,
            '    <TextBlock Grid.Column="0" Text="{x:Static res:Strings.Label_Position}" Style="{StaticResource LfeLabel}" VerticalAlignment="Center"/>',
            "    " + CALC.format(col=2, name=f"{p}FrequencyCalc"),
            "    " + FX_BUTTON.format(col=3, tag=f"{tag}:frequency"),
            "</Grid>",
            FORMULA_TEXT.format(name=f"{p}FrequencyFormulaText"),
        ]
    else:
        body += slider_param(p, tag, "Frequency", "SliderLabel_FrequencyHz",
                             f"{p}FrequencySlider", f"{p}FrequencyValue", 10, 100, "S12Frequency_ValueChanged")
        body += [
            f'<StackPanel x:Name="{p}FreqLimits" Visibility="Collapsed" Margin="0,4,0,0">',
            "    <Grid>",
            "        " + TWO_COLS,
            '        <TextBlock Grid.Column="0" Text="{x:Static res:Strings.Label_FormulaLimitsHz}" Style="{StaticResource LabelText}" VerticalAlignment="Center"/>',
            f'        <TextBlock Grid.Column="1" x:Name="{p}FreqRangeText" Foreground="{{StaticResource CyanBrush}}" '
            'FontFamily="{StaticResource FontMono}" FontSize="11" VerticalAlignment="Center"/>',
            "    </Grid>",
            f'    <ctrl:MozaRangeSlider x:Name="{p}FreqRange" Tag="{tag}" Template="{{StaticResource MozaRangeSliderThinTemplate}}" '
            'MinHeight="0" Height="30" EdgePad="8" NodeSize="22" TrackThickness="6" Minimum="10" Maximum="100" '
            'LowValue="10" HighValue="100" RangeChanged="S12FreqRange_RangeChanged"/>',
            "</StackPanel>",
        ]
    body += slider_param(p, tag, "Intensity", "SliderLabel_Intensity",
                         f"{p}Intensity", f"{p}IntensityValue", 0, 100, "S12Intensity_ValueChanged")
    body += [
        f'<StackPanel x:Name="{p}EdgeOptions" Visibility="Collapsed">',
        '    <Grid Margin="0,6,0,0">',
        "        " + TWO_COLS,
        '        <TextBlock Grid.Column="0" Text="{x:Static res:Strings.SliderLabel_PulseLength}" Style="{StaticResource LfeLabel}" VerticalAlignment="Center"/>',
        f'        <TextBox Grid.Column="1" x:Name="{p}PulseValue" Width="58" Margin="0,0,38,0" Style="{{StaticResource SliderValueEditBox}}" '
        f'Tag="{{Binding ElementName={p}PulseSlider}}" KeyDown="SliderValueBox_KeyDown" LostFocus="SliderValueBox_LostFocus"/>',
        "    </Grid>",
        f'    <Slider x:Name="{p}PulseSlider" Tag="{tag}" Style="{{StaticResource SettingSlider}}" Minimum="20" Maximum="1000" '
        'TickFrequency="10" SmallChange="10" LargeChange="50" IsSnapToTickEnabled="True" Margin="0,2,0,0" ValueChanged="S12Pulse_ValueChanged"/>',
        '    <Grid Margin="0,6,0,0">',
        "        " + TWO_COLS,
        '        <TextBlock Grid.Column="0" Text="{x:Static res:Strings.SliderLabel_VibrateOnNeutral}" Style="{StaticResource LfeLabel}" VerticalAlignment="Center"/>',
        f'        <ctrl:OffOnToggle Grid.Column="1" x:Name="{p}VibrateOnNeutral" Tag="{tag}" Margin="0,0,38,0" VerticalAlignment="Center" '
        'Checked="S12VibrateOnNeutral_Changed" Unchecked="S12VibrateOnNeutral_Changed"/>',
        "    </Grid>",
        '    <Grid Margin="0,6,0,0">',
        "        " + TWO_COLS,
        '        <TextBlock Grid.Column="0" Text="{x:Static res:Strings.SliderLabel_ShiftDebounce}" Style="{StaticResource LfeLabel}" VerticalAlignment="Center"/>',
        f'        <TextBox Grid.Column="1" x:Name="{p}DebounceValue" Width="58" Margin="0,0,38,0" Style="{{StaticResource SliderValueEditBox}}" '
        f'Tag="{{Binding ElementName={p}DebounceSlider}}" KeyDown="SliderValueBox_KeyDown" LostFocus="SliderValueBox_LostFocus"/>',
        "    </Grid>",
        f'    <Slider x:Name="{p}DebounceSlider" Tag="{tag}" Style="{{StaticResource SettingSlider}}" Minimum="0" Maximum="1000" '
        'TickFrequency="50" SmallChange="50" LargeChange="50" IsSnapToTickEnabled="True" Margin="0,2,0,0" ValueChanged="S12Debounce_ValueChanged"/>',
        "</StackPanel>",
    ]
    lines += ["        " + b for b in body]
    lines += ["    </StackPanel>", "</ctrl:SectionCard>"]
    return lines


def output_card():
    legend = []
    for i, (color, key) in enumerate((("#FF3CE0E0", "Option_Throttle"),
                                      ("#FFFFB84D", "Option_Brake"),
                                      ("#FF8BD866", "Option_Clutch"))):
        right = 12 if i < 2 else 0
        legend += [
            f'        <Rectangle Width="10" Height="10" Fill="{color}" VerticalAlignment="Center"/>',
            f'        <TextBlock Text="{{x:Static res:Strings.{key}}}" Foreground="{{StaticResource TextDimBrush}}" '
            f'FontSize="11" Margin="4,0,{right},0" VerticalAlignment="Center"/>',
        ]
    return [
        '<ctrl:SectionCard Grid.Row="0" Grid.Column="0" VerticalAlignment="Top" '
        'Title="{x:Static res:Strings.Section_PedalHapticsOutput}" Icon="{StaticResource IconShift}" Margin="0,0,7,14">',
        "    <ctrl:SectionCard.Header>",
        '        <Button Content="{x:Static res:Strings.Label_Test}" Padding="10,2" Click="S12TestAll_Click"/>',
        "    </ctrl:SectionCard.Header>",
        "    <StackPanel>",
        '        <ctrl:LfeScope x:Name="S12Scope" Height="168" MaxHz="100"/>',
        '        <StackPanel Orientation="Horizontal" Margin="0,5,0,0">',
        *["    " + l for l in legend],
        "        </StackPanel>",
        "    </StackPanel>",
        "</ctrl:SectionCard>",
    ]


def generate(indent):
    rows = (len(CHANNELS) + 1 + 1) // 2
    out = [
        "<Grid>",
        '    <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>',
        "    <Grid.RowDefinitions>" + '<RowDefinition Height="Auto"/>' * rows + "</Grid.RowDefinitions>",
    ]
    out += ["    " + l for l in output_card()]
    for i, (tag, cap, title) in enumerate(CHANNELS):
        out += ["    " + l for l in card(i, tag, cap, title)]
    out.append("</Grid>")
    return "".join(indent + l + "\n" for l in out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args()

    with open(XAML, "r", encoding="utf-8") as f:
        text = f.read()
    s = text.find(START)
    e = text.find(END)
    if s < 0 or e < 0 or e < s:
        sys.exit("S12-GEN markers not found in SettingsControl.xaml")

    line_start = text.rfind("\n", 0, s) + 1
    indent = text[line_start:s]
    head = text[: s + len(START)] + "\n"
    tail = indent + text[e:]
    new = head + generate(indent) + tail
    if new == text:
        print("S12 cards up to date")
        return
    if args.check:
        sys.exit("S12 cards out of date — run tools/rebuild_s12_cards.py")
    with open(XAML, "w", encoding="utf-8", newline="") as f:
        f.write(new)
    print("S12 cards regenerated")


if __name__ == "__main__":
    main()
