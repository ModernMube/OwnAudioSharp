#!/bin/bash

# Beállítjuk a keresés kiindulási könyvtárát (ahol a script fut)
search_path="$(cd "$(dirname "$0")" && pwd)"

echo ""
echo "======================================================="
echo "Inditas: Mappa tisztitasa a kovetkezo helyen:"
echo "$search_path"
echo "======================================================="
echo ""

# -----------------------------------------------------------
# 1. bin mappák törlése
# -----------------------------------------------------------
echo "bin mappak keresese es torlese..."
find "$search_path" -type d -name "bin" -print0 | while IFS= read -r -d '' dir; do
    echo "Torles: $dir"
    rm -rf "$dir"
done

echo ""

# -----------------------------------------------------------
# 2. obj mappák törlése
# -----------------------------------------------------------
echo "obj mappak keresese es torlese..."
find "$search_path" -type d -name "obj" -print0 | while IFS= read -r -d '' dir; do
    echo "Torles: $dir"
    rm -rf "$dir"
done

echo ""
echo "======================================================="
echo "Tisztitas befejezve!"
echo "======================================================="