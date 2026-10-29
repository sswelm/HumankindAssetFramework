#!/usr/bin/env perl
# shape-scan.pl — ONE process for the parse-shape and member-shape gates (2026-10-29).
#
# Both gates strip comments, collapse whitespace and run a PCRE over every production C# file. Until this file each
# did that per file in bash (perl, tr, grep -oP, two subshells: six to eight forks a file, 340 files), and took 67 s
# and 99 s on a tree the regexes themselves scan in under a second - and the reader-gate drill, which runs the
# member gate three times, 226 s. On Windows a fork is the cost. The regexes are the gates' own, unchanged, with
# grep -oP's "every non-overlapping match, each on its own line" as `while (/re/g)`; the output is the gates' own
# FAIL lines (the file, the match cut to 140 characters) followed by one `HITS <n>` line the caller reads.
#
#   perl tools/shape-scan.pl parse  <file>...
#   perl tools/shape-scan.pl member <reader regex> <file>...
use strict; use warnings;
use Encode qw(decode FB_CROAK);
binmode STDOUT, ":encoding(UTF-8)";

my $mode = shift @ARGV // '';
die "shape-scan.pl: mode parse|member\n" unless $mode eq 'parse' || $mode eq 'member';
my $reader = $mode eq 'member' ? shift @ARGV : undef;
die "shape-scan.pl: member needs the reader regex\n" if $mode eq 'member' && !defined $reader;
my $types = qr/(?:float|double|int|long|uint|ulong|short|byte|decimal|bool)/;
my $hits = 0;
for my $f (@ARGV) {
    open(my $fh, '<:raw', $f) or die "shape-scan.pl: cannot read $f: $!\n";
    my $s = do { local $/; <$fh> }; close $fh;
    $s = '' unless defined $s;
    # Roslyn accepts BOM-marked UTF-16; the former tr -d NUL pass reached its ASCII shapes too.
    if ($s =~ s/^\xFF\xFE//) { $s = decode('UTF-16LE', $s, FB_CROAK); }
    elsif ($s =~ s/^\xFE\xFF//) { $s = decode('UTF-16BE', $s, FB_CROAK); }
    else { $s = decode('UTF-8', $s, FB_CROAK); $s =~ s/^\x{FEFF}//; }
    $s =~ s/\0//g;
    # Strip comments first — the policy notes quote the banned shapes verbatim, and a gate that trips on its own
    # documentation is a gate nobody keeps. Then collapse whitespace so the two-line form reads like the one-liner.
    $s =~ s{/\*.*?\*/}{}gs; $s =~ s{//[^\n]*}{}g; $s =~ s{\s+}{ }g;
    my @found;
    if ($mode eq 'parse') {
        # <type> <name> = <literal…> ;   <type>.TryParse( … out <same name> ) — the back-reference is the whole test
        while ($s =~ /\b$types\s+(\w+)\s*=\s*[^;{}]+;\s*$types\.TryParse\([^;]*?\bout\s+\1\b/g) { push @found, $&; }
    } else {
        # (a) DEAD INITIALIZER: <type> <var> = <init>; try { <var> = Convert.To*(<reader>(... — not when `??` supplies the fallback
        while ($s =~ /\b$types\s+(\w+)\s*=\s*[^;{}]+;\s*try\s*\{\s*\1\s*=\s*(?:\w+\s*\(\s*)?Convert\.To\w+\s*\(\s*$reader(?![^{}]*\?\?)[^{}]*?\}\s*catch/g) { push @found, $&; }
        # (b) PHANTOM SKIP: try { ... Convert.To*(<reader>(...)) ... } catch { continue; } — the continue never fires
        while ($s =~ /try\s*\{[^{}]*Convert\.To\w+\s*\(\s*$reader[^{}]*\}\s*catch\s*(?:\([^)]*\))?\s*\{\s*continue\s*;\s*\}/g) { push @found, $&; }
    }
    for my $m (@found) {
        $hits++;
        (my $name = $f) =~ s{^\./}{};
        printf "FAIL: %s\n      %s\n", $name, substr($m, 0, 140);
    }
}
print "HITS $hits\n";
