// Shared list sorting for Library and Downloads screens.
// Sort options mirror the desktop folder-tree dropdown (same labels/order).

enum LibSort { nameAsc, nameDesc, createdOldest, createdNewest, modifiedOldest, modifiedNewest }

const Map<LibSort, String> libSortLabels = {
  LibSort.nameAsc: 'Name (A-Z)',
  LibSort.nameDesc: 'Name (Z-A)',
  LibSort.createdOldest: 'Created (Oldest)',
  LibSort.createdNewest: 'Created (Newest)',
  LibSort.modifiedOldest: 'Modified (Oldest)',
  LibSort.modifiedNewest: 'Modified (Newest)',
};

// Dates sort with nulls always last, regardless of direction.
int cmpDateNullsLast(int? a, int? b, bool desc) {
  if (a == null && b == null) return 0;
  if (a == null) return 1;
  if (b == null) return -1;
  return desc ? b.compareTo(a) : a.compareTo(b);
}

List<T> sortedBy<T>(List<T> src, LibSort sort, String Function(T) name,
    int? Function(T) created, int? Function(T) modified) {
  final list = [...src];
  int byName(T a, T b) => name(a).toLowerCase().compareTo(name(b).toLowerCase());
  switch (sort) {
    case LibSort.nameAsc:
      list.sort(byName);
    case LibSort.nameDesc:
      list.sort((a, b) => byName(b, a));
    case LibSort.createdOldest:
      list.sort((a, b) => cmpDateNullsLast(created(a), created(b), false));
    case LibSort.createdNewest:
      list.sort((a, b) => cmpDateNullsLast(created(a), created(b), true));
    case LibSort.modifiedOldest:
      list.sort((a, b) => cmpDateNullsLast(modified(a), modified(b), false));
    case LibSort.modifiedNewest:
      list.sort((a, b) => cmpDateNullsLast(modified(a), modified(b), true));
  }
  return list;
}
