<?php
// "TeamNotes" — a tiny notes service used as an Attack & Defense target.
//
// Notes live as files under /tmp/notes/<id>. The "admin" note proxies the
// live, rotating flag file. The intended VULNERABILITY: there is no
// authorization on note ids, and the id is concatenated straight into a
// filesystem path — so any attacker can
//   * read the protected admin note:   GET /note?id=admin   (IDOR), and
//   * traverse the filesystem:          GET /note?id=../../etc/passwd (LFI).
// Defenders are expected to add auth on `admin` and sanitise the id while
// keeping the SLA checker (store/retrieve + admin readback) green.

$flagfile = getenv('GZCTF_FLAG_FILE') ?: '/flag';
$dir = '/tmp/notes';
@mkdir($dir, 0777, true);

$path = parse_url($_SERVER['REQUEST_URI'], PHP_URL_PATH);
parse_str($_SERVER['QUERY_STRING'] ?? '', $q);
$id = isset($q['id']) ? (string)$q['id'] : '';

if ($path === '/health') {
    echo 'ok';
    return true;
}

if ($path === '/note') {
    if ($id === 'admin') {                       // should require auth — it doesn't
        echo @file_get_contents($flagfile);
        return true;
    }
    $target = $dir . '/' . $id;                   // unsanitised → path traversal
    if (is_file($target)) {
        echo @file_get_contents($target);
        return true;
    }
    http_response_code(404);
    echo 'no such note';
    return true;
}

if ($path === '/save' && $_SERVER['REQUEST_METHOD'] === 'POST') {
    // The save path *is* sanitised (no slashes) so the SLA store/retrieve is
    // stable; the read path above is the deliberately-vulnerable one.
    if ($id === '' || strpos($id, '/') !== false || $id === 'admin') {
        http_response_code(400);
        echo 'bad id';
        return true;
    }
    file_put_contents($dir . '/' . $id, file_get_contents('php://input'));
    echo 'saved';
    return true;
}

echo "TeamNotes — POST /save?id=<id> ; GET /note?id=<id> ; GET /health";
return true;
