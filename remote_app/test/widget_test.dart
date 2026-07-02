import 'package:flutter_test/flutter_test.dart';
import 'package:music_organiser_remote/models.dart';
import 'package:music_organiser_remote/theme.dart';

void main() {
  test('fmtTime formats mm:ss and clamps negatives', () {
    expect(fmtTime(0), '0:00');
    expect(fmtTime(72), '1:12');
    expect(fmtTime(3599), '59:59');
    expect(fmtTime(-5), '0:00');
  });

  test('TrackFile parses rating, tags and derives name/subtitle', () {
    final t = TrackFile.fromJson({
      'path': r'F:\Music\Daft Punk\Around the World.mp3',
      'title': 'Around the World',
      'artist': 'Daft Punk',
      'album': 'Homework',
      'tags': 'house, fav',
      'durationSec': 429,
      'rating': 5,
      'isPlaying': true,
    });
    expect(t.name, 'Around the World');
    expect(t.subtitle, 'Daft Punk — Homework');
    expect(t.rating, 5);
    expect(t.tagList, ['house', 'fav']);
    expect(t.isPlaying, true);
  });

  test('TrackFile tolerates missing/null fields', () {
    final t = TrackFile.fromJson({'path': r'C:\a\b.flac'});
    expect(t.rating, isNull);
    expect(t.tagList, isEmpty);
    expect(t.name, 'b.flac');
  });
}
