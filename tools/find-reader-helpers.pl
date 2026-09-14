#!/usr/bin/env perl
# find-reader-helpers.pl — discover every HAF helper that reads a game member BY NAME on the caller's behalf:
#
#     static <T> Name(object x, string name, …) { … GetMember(x, name) … }
#
# and print "Name<TAB>file:line" for each. Shared by tools/check-catalog.sh (every return type) and
# tools/check-member-shape.sh (--object-only: the wrappers a Convert.To*() can sit on). Both gates compare the
# result against their hand-maintained helper list and FAIL on a name they do not know — the list that decides what
# a gate can see is itself gated.
#
# The WHOLE method body is scanned (2026-09-14 review of PR #48): the first version looked at the declaration plus
# three lines, and a wrapper with a null guard above its read
#
#     static object Peek(object o, string name)
#     {
#         if (o == null)
#             return null;
#         return GetMember(o, name);      // line 5 — outside a 3-line window
#     }
#
# passed both self-checks, which is exactly the silent gap the self-check exists to close. Bodies are found by
# walking to the matching brace (expression-bodied `=> …;` up to the `;`).
#
# Comments and string/char literals are BLANKED first (second review of PR #48): a `}` inside a comment or a string
# above the read — `// }` or `"}"` — closed the body early and the wrapper went undiscovered again. The first version
# had even claimed a brace in a literal could only make a body longer; it can just as well make it shorter, and
# shorter is the silent direction. Blanking keeps every newline and the length, so offsets and line numbers hold.
# Drilled by tools/drill-reader-gates.sh (plain null guard, comment brace, string brace).
#
#   perl tools/find-reader-helpers.pl [--object-only] <file.cs>...
use strict;
use warnings;

my $objectOnly = 0;
if (@ARGV && $ARGV[0] eq '--object-only') { shift @ARGV; $objectOnly = 1; }
my $ret = $objectOnly ? 'object' : '[A-Za-z_][\w<>\[\],?]*';
my $readers = qr/\b(?:GetMember|GetMemberOrNull|Mem|Member|TryConvert|CachedMember|CachedField|CachedProp|GFA?|GetField|GetProperty|GetMethod|AccessTools\.\w+)\s*\(/;

# Replace the CONTENT of block/line comments, verbatim/plain strings, char literals and the TEXT of interpolated
# strings with spaces. Interpolation HOLES (`{expr}`) are code and stay — `$"… '{GetMember(mat, "name")}' …"` is a
# real reader call (DumpSelectorElements has exactly that), and a first regex version that blanked the whole string
# silently lost it. Holes may nest strings and further interpolations, so this is a small state machine with a stack
# rather than a regex. Escaped `{{`/`}}` in interpolated text are blanked; a hole's own braces are balanced code.
sub blank_noncode {
    my ($s) = @_;
    my $n = length $s;
    my @c = split //, $s;
    my @stack;   # one entry per open interpolated string: [verbatim, holeDepth]; holeDepth > 0 = inside a hole (code)
    my $blank = sub { my ($a, $b) = @_; $b = $n - 1 if $b > $n - 1; for my $k ($a .. $b) { $c[$k] = ' ' if $c[$k] ne "\n" } };
    my $i = 0;
    while ($i < $n) {
        my $ch = $c[$i];
        my $nx = $i + 1 < $n ? $c[$i + 1] : '';
        if (@stack && $stack[-1][1] == 0) {                       # interpolated string TEXT
            my $verb = $stack[-1][0];
            if ($ch eq '{' && $nx eq '{') { $blank->($i, $i + 1); $i += 2; next }
            if ($ch eq '}' && $nx eq '}') { $blank->($i, $i + 1); $i += 2; next }
            if ($ch eq '{') { $stack[-1][1] = 1; $i++; next }    # hole opens — brace kept as code
            if ($ch eq '"') {
                if ($verb && $nx eq '"') { $blank->($i, $i + 1); $i += 2; next }
                $c[$i] = ' '; pop @stack; $i++; next;              # string ends
            }
            if (!$verb && $ch eq '\\') { $blank->($i, $i + 1); $i += 2; next }
            $c[$i] = ' ' if $ch ne "\n";
            $i++; next;
        }
        # CODE — top level or inside an interpolation hole
        if ($ch eq '/' && $nx eq '/') { my $e = index($s, "\n", $i); $e = $n if $e < 0; $blank->($i, $e - 1); $i = $e; next }
        if ($ch eq '/' && $nx eq '*') { my $e = index($s, '*/', $i + 2); $e = $e < 0 ? $n : $e + 2; $blank->($i, $e - 1); $i = $e; next }
        if ($ch eq "'") {
            my $j = $i + 1;
            while ($j < $n && $c[$j] ne "'" && $c[$j] ne "\n") { $j++ if $c[$j] eq '\\'; $j++ }
            $blank->($i, $j); $i = $j + 1; next;
        }
        if (($ch eq '$' && $nx eq '"') || (($ch eq '$' || $ch eq '@') && ($nx eq '@' || $nx eq '$') && $i + 2 < $n && $c[$i + 2] eq '"')) {
            my $len = $nx eq '"' ? 2 : 3;
            push @stack, [$len == 3 ? 1 : 0, 0];
            $blank->($i, $i + $len - 1); $i += $len; next;
        }
        if ($ch eq '@' && $nx eq '"') {                           # verbatim string: "" is the only escape
            my $j = $i + 2;
            while ($j < $n) { if ($c[$j] eq '"') { if ($j + 1 < $n && $c[$j + 1] eq '"') { $j += 2; next } last } $j++ }
            $blank->($i, $j); $i = $j + 1; next;
        }
        if ($ch eq '"') {                                         # plain string
            my $j = $i + 1;
            while ($j < $n && $c[$j] ne '"' && $c[$j] ne "\n") { $j++ if $c[$j] eq '\\'; $j++ }
            $blank->($i, $j); $i = $j + 1; next;
        }
        if (@stack) {                                             # inside a hole: find its closing brace
            if    ($ch eq '{') { $stack[-1][1]++ }
            elsif ($ch eq '}') { $stack[-1][1]-- }                # 0 → back to the string's text
        }
        $i++;
    }
    return join '', @c;
}

for my $f (@ARGV) {
    open(my $fh, '<', $f) or next;
    my $src = do { local $/; <$fh> };
    close $fh;
    $src =~ s/\r//g;
    $src = blank_noncode($src);
    while ($src =~ /^[ \t]*(?:internal\s+|public\s+|private\s+)?static\s+$ret\s+(\w+)\s*\(\s*(?:this\s+)?object\s+\w+\s*,\s*string\s+\w+/mg) {
        my $name  = $1;
        my $start = $-[0];
        my $pos   = pos($src);
        my $line  = 1 + (substr($src, 0, $start) =~ tr/\n//);
        my $body;
        my $brace = index($src, '{', $pos);
        my $arrow = index($src, '=>', $pos);
        if ($arrow >= 0 && ($brace < 0 || $arrow < $brace)) {
            my $end = index($src, ';', $arrow);
            $end = length($src) if $end < 0;
            $body = substr($src, $arrow, $end - $arrow);
        }
        elsif ($brace >= 0) {
            my $depth = 0;
            my $j = $brace;
            for (; $j < length($src); $j++) {
                my $c = substr($src, $j, 1);
                if ($c eq '{') { $depth++ }
                elsif ($c eq '}') { $depth--; last if $depth == 0 }
            }
            $body = substr($src, $brace, $j - $brace + 1);
        }
        else { next }
        print "$name\t$f:$line\n" if $body =~ $readers;
        pos($src) = $pos;
    }
}
