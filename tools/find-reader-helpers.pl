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
# walking to the matching brace (expression-bodied `=> …;` up to the `;`); a brace inside a string literal can only
# make a body LONGER, i.e. over-report a helper — the loud direction. Drilled by tools/drill-reader-gates.sh.
#
#   perl tools/find-reader-helpers.pl [--object-only] <file.cs>...
use strict;
use warnings;

my $objectOnly = 0;
if (@ARGV && $ARGV[0] eq '--object-only') { shift @ARGV; $objectOnly = 1; }
my $ret = $objectOnly ? 'object' : '[A-Za-z_][\w<>\[\],?]*';
my $readers = qr/\b(?:GetMember|GetMemberOrNull|Mem|Member|TryConvert|CachedMember|CachedField|CachedProp|GFA?|GetField|GetProperty|GetMethod|AccessTools\.\w+)\s*\(/;

for my $f (@ARGV) {
    open(my $fh, '<', $f) or next;
    my $src = do { local $/; <$fh> };
    close $fh;
    $src =~ s/\r//g;
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
