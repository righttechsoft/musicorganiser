namespace MusicOrganiser.Models;

// A selectable audio output device (CoreAudio endpoint). Value equality on Id keeps
// ComboBox SelectedItem matching across list rebuilds.
public record OutputDeviceItem(string Id, string Name);
