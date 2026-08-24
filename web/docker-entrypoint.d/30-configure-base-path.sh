#!/bin/sh
set -eu

base_path="${BASE_PATH:-/}"

case "$base_path" in
    /*) ;;
    *)
        echo >&2 "BASE_PATH must start with '/' (for example: /app)"
        exit 1
        ;;
esac

# Nginx locations use one canonical form: no trailing slash for the redirect,
# and exactly one trailing slash for the static-file locations.
while [ "$base_path" != "/" ] && [ "${base_path%/}" != "$base_path" ]; do
    base_path="${base_path%/}"
done

case "$base_path" in
    *[!A-Za-z0-9_./~-]*)
        echo >&2 "BASE_PATH contains unsupported characters: $base_path"
        exit 1
        ;;
esac

if [ "$base_path" = "/" ]; then
    base_path_slash="/"
    redirect=""
else
    base_path_slash="$base_path/"
    redirect="    location = $base_path { return 308 $base_path_slash; }"
fi

sed \
    -e "s|@@BASE_PATH_REDIRECT@@|$redirect|" \
    -e "s|@@BASE_PATH_SLASH@@|$base_path_slash|g" \
    /etc/nginx/templates/default.conf.template \
    > /etc/nginx/conf.d/default.conf

echo "Configured KawaPaint at base path $base_path_slash"
