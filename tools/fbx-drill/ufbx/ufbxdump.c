// ufbxdump - the ORACLE of the native FBX reader's object layer: what ufbx (the reference reader) makes of a file.
// One line per node, mesh, material; doubles as their IEEE-754 bits (hex), streams as FNV-1a 64 hashes over the
// bits of their values; --full prints every row of every stream. Default load options (no axis or unit conversion,
// inherit modes preserved): the file's own frame, as the specification reads it.
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <math.h>
#include <windows.h>
#include "ufbx.h"

// THE SAME COS AND SIN AS THE C# SIDE (2026-11-01): MinGW-w64's runtime defines its own sin/cos (nm shows `T sin`,
// `T cos` inside the exe), one ulp from the Windows CRT's on some angles - and FbxMath P/Invokes ucrtbase's. Built with
// -DUFBX_EXTERNAL_MATH, ufbx calls these, which resolve every transcendental from ucrtbase.dll at start, so both readers
// run the one implementation. The exact operations (fabs, fmin, copysign, floor...) are plain C.
typedef double (*fn1)(double); typedef double (*fn2)(double, double);
static fn1 p_sqrt, p_sin, p_cos, p_tan, p_asin, p_acos, p_atan, p_rint; static fn2 p_atan2, p_pow, p_nextafter;
static void ucrt_init(void) {
	static int done = 0; if (done) return; done = 1;
	HMODULE m = LoadLibraryA("ucrtbase.dll");
	if (!m) { fprintf(stderr, "ucrtbase.dll not found\n"); exit(3); }
	p_sqrt = (fn1)GetProcAddress(m, "sqrt"); p_sin = (fn1)GetProcAddress(m, "sin"); p_cos = (fn1)GetProcAddress(m, "cos"); p_tan = (fn1)GetProcAddress(m, "tan");
	p_asin = (fn1)GetProcAddress(m, "asin"); p_acos = (fn1)GetProcAddress(m, "acos"); p_atan = (fn1)GetProcAddress(m, "atan"); p_rint = (fn1)GetProcAddress(m, "rint");
	p_atan2 = (fn2)GetProcAddress(m, "atan2"); p_pow = (fn2)GetProcAddress(m, "pow"); p_nextafter = (fn2)GetProcAddress(m, "nextafter");
	if (!p_sqrt || !p_sin || !p_cos || !p_tan || !p_asin || !p_acos || !p_atan || !p_rint || !p_atan2 || !p_pow || !p_nextafter) { fprintf(stderr, "ucrtbase.dll lacks a math export\n"); exit(3); }
}
double ufbx_sqrt(double x) { ucrt_init(); return p_sqrt(x); }
double ufbx_sin(double x) { ucrt_init(); return p_sin(x); }
double ufbx_cos(double x) { ucrt_init(); return p_cos(x); }
double ufbx_tan(double x) { ucrt_init(); return p_tan(x); }
double ufbx_asin(double x) { ucrt_init(); return p_asin(x); }
double ufbx_acos(double x) { ucrt_init(); return p_acos(x); }
double ufbx_atan(double x) { ucrt_init(); return p_atan(x); }
double ufbx_atan2(double y, double x) { ucrt_init(); return p_atan2(y, x); }
double ufbx_pow(double x, double y) { ucrt_init(); return p_pow(x, y); }
double ufbx_rint(double x) { ucrt_init(); return p_rint(x); }
double ufbx_nextafter(double x, double y) { ucrt_init(); return p_nextafter(x, y); }
double ufbx_fmin(double a, double b) { return a < b ? a : b; }
double ufbx_fmax(double a, double b) { return a > b ? a : b; }
double ufbx_fabs(double x) { return fabs(x); }
double ufbx_copysign(double x, double y) { return copysign(x, y); }
double ufbx_floor(double x) { return floor(x); }
double ufbx_ceil(double x) { return ceil(x); }
bool ufbx_isnan(double x) { return x != x; }

static uint64_t fnv = 14695981039346656037ULL;   // FNV-1a 64's offset basis (the first draft dropped a digit: every mesh hash differed from the C# side's, nothing else)
static void hash_reset(void) { fnv = 14695981039346656037ULL; }
static void hash_bytes(const void *p, size_t n) { const uint8_t *b = (const uint8_t*)p; for (size_t i = 0; i < n; i++) { fnv ^= b[i]; fnv *= 1099511628211ULL; } }
static void hash_double(double d) { uint64_t u; memcpy(&u, &d, 8); hash_bytes(&u, 8); }
static void hash_u32(uint32_t v) { hash_bytes(&v, 4); }
static uint64_t bits(double d) { uint64_t u; memcpy(&u, &d, 8); return u; }

static int full = 0;

static void print_vec3(const char *tag, ufbx_vec3 v) { printf(" %s=%016llx,%016llx,%016llx", tag, (unsigned long long)bits(v.x), (unsigned long long)bits(v.y), (unsigned long long)bits(v.z)); }
static void print_quat(const char *tag, ufbx_quat q) { printf(" %s=%016llx,%016llx,%016llx,%016llx", tag, (unsigned long long)bits(q.x), (unsigned long long)bits(q.y), (unsigned long long)bits(q.z), (unsigned long long)bits(q.w)); }
static void print_matrix(const char *tag, const ufbx_matrix *m) { printf("  %s", tag); for (int i = 0; i < 12; i++) printf(" %016llx", (unsigned long long)bits(m->v[i])); printf("\n"); }

static const char *attrib_name(ufbx_element_type t) {
	switch (t) {
	case UFBX_ELEMENT_MESH: return "mesh"; case UFBX_ELEMENT_BONE: return "bone"; case UFBX_ELEMENT_EMPTY: return "empty";
	case UFBX_ELEMENT_LIGHT: return "light"; case UFBX_ELEMENT_CAMERA: return "camera"; case UFBX_ELEMENT_UNKNOWN: return "none";
	default: return "other";
	}
}

static void dump_stream_vec3(const char *tag, const ufbx_vertex_vec3 *s, size_t num_indices) {
	if (!s->exists) { printf("  %s none\n", tag); return; }
	hash_reset();
	for (size_t i = 0; i < num_indices; i++) {
		ufbx_vec3 v = ufbx_get_vertex_vec3(s, i);
		hash_double(v.x); hash_double(v.y); hash_double(v.z);
		if (full) printf("  %s %zu %016llx %016llx %016llx\n", tag, i, (unsigned long long)bits(v.x), (unsigned long long)bits(v.y), (unsigned long long)bits(v.z));
	}
	printf("  %s values=%zu indices=%zu unique=%s hash=%016llx\n", tag, s->values.count, s->indices.count, s->unique_per_vertex ? "1" : "0", (unsigned long long)fnv);
}
static void dump_stream_vec2(const char *tag, const ufbx_vertex_vec2 *s, size_t num_indices) {
	if (!s->exists) { printf("  %s none\n", tag); return; }
	hash_reset();
	for (size_t i = 0; i < num_indices; i++) {
		ufbx_vec2 v = ufbx_get_vertex_vec2(s, i);
		hash_double(v.x); hash_double(v.y);
		if (full) printf("  %s %zu %016llx %016llx\n", tag, i, (unsigned long long)bits(v.x), (unsigned long long)bits(v.y));
	}
	printf("  %s values=%zu indices=%zu unique=%s hash=%016llx\n", tag, s->values.count, s->indices.count, s->unique_per_vertex ? "1" : "0", (unsigned long long)fnv);
}
static void dump_stream_vec4(const char *tag, const ufbx_vertex_vec4 *s, size_t num_indices) {
	if (!s->exists) { printf("  %s none\n", tag); return; }
	hash_reset();
	for (size_t i = 0; i < num_indices; i++) {
		ufbx_vec4 v = ufbx_get_vertex_vec4(s, i);
		hash_double(v.x); hash_double(v.y); hash_double(v.z); hash_double(v.w);
		if (full) printf("  %s %zu %016llx %016llx %016llx %016llx\n", tag, i, (unsigned long long)bits(v.x), (unsigned long long)bits(v.y), (unsigned long long)bits(v.z), (unsigned long long)bits(v.w));
	}
	printf("  %s values=%zu indices=%zu unique=%s hash=%016llx\n", tag, s->values.count, s->indices.count, s->unique_per_vertex ? "1" : "0", (unsigned long long)fnv);
}

static int dump_main(int argc, char **argv) {
	int first = 1, reject = 0, failed = 0;
	if (argc > 1 && strcmp(argv[1], "--full") == 0) { full = 1; first = 2; }
	if (argc > 1 && strcmp(argv[1], "--reject") == 0) { reject = 1; first = 2; }
	if (argc <= first) { fprintf(stderr, "no FBX scene files selected\n"); return 2; }
	for (int a = first; a < argc; a++) {
		printf("=== %s\n", argv[a]);
		ufbx_load_opts opts = { 0 };
		ufbx_error error;
		ufbx_scene *scene = ufbx_load_file(argv[a], &opts, &error);
		if (reject) {
			if (scene) { printf("ERROR accepted malformed scene\n"); ufbx_free_scene(scene); failed = 1; }
			else { printf("REFUSED %s\n", error.description.data); }
			continue;
		}
		if (!scene) { printf("ERROR %s\n", error.description.data); failed = 1; continue; }
		printf("settings up=%d front=%d right=%d unit=%016llx fps=%016llx version=%u\n", (int)scene->settings.axes.up, (int)scene->settings.axes.front, (int)scene->settings.axes.right,
			(unsigned long long)bits(scene->settings.unit_meters), (unsigned long long)bits(scene->settings.frames_per_second), scene->metadata.version);
		printf("counts nodes=%zu meshes=%zu materials=%zu textures=%zu skins=%zu clusters=%zu bones=%zu anim_stacks=%zu\n",
			scene->nodes.count, scene->meshes.count, scene->materials.count, scene->textures.count, scene->skin_deformers.count, scene->skin_clusters.count, scene->bones.count, scene->anim_stacks.count);
		for (size_t i = 0; i < scene->nodes.count; i++) {
			ufbx_node *n = scene->nodes.data[i];
			printf("node %zu \"%s\" parent=%d attrib=%s mesh=%d inherit=%d order=%d rotspace=%d geomxf=%d visible=%d\n", i, n->name.data, n->parent ? (int)n->parent->typed_id : -1,
				attrib_name(n->attrib_type), n->mesh ? (int)n->mesh->typed_id : -1, (int)n->inherit_mode, (int)n->rotation_order, n->use_rotation_space ? 1 : 0, n->has_geometry_transform ? 1 : 0, n->visible ? 1 : 0);
			printf("  local"); print_vec3("t", n->local_transform.translation); print_quat("r", n->local_transform.rotation); print_vec3("s", n->local_transform.scale); printf("\n");
			print_matrix("n2p", &n->node_to_parent);
			print_matrix("n2w", &n->node_to_world);
			if (n->has_geometry_transform) print_matrix("g2n", &n->geometry_to_node);
		}
		for (size_t i = 0; i < scene->meshes.count; i++) {
			ufbx_mesh *m = scene->meshes.data[i];
			printf("mesh %zu \"%s\" vertices=%zu faces=%zu indices=%zu triangles=%zu max_face=%u materials=%zu instances=%zu\n", i, m->name.data, m->num_vertices, m->num_faces, m->num_indices, m->num_triangles, m->max_face_triangles, m->materials.count, m->instances.count);
			hash_reset();
			for (size_t f = 0; f < m->num_faces; f++) {
				ufbx_face face = m->faces.data[f];
				hash_u32(face.num_indices);
				for (uint32_t k = 0; k < face.num_indices; k++) hash_u32(m->vertex_indices.data[face.index_begin + k]);
				if (full) { printf("  face %zu n=%u mat=%u idx=", f, face.num_indices, m->face_material.count ? m->face_material.data[f] : 0); for (uint32_t k = 0; k < face.num_indices; k++) printf("%s%u", k ? "," : "", m->vertex_indices.data[face.index_begin + k]); printf("\n"); }
			}
			printf("  faces hash=%016llx\n", (unsigned long long)fnv);
			hash_reset(); for (size_t f = 0; f < m->face_material.count; f++) hash_u32(m->face_material.data[f]);
			printf("  face_material count=%zu hash=%016llx\n", m->face_material.count, (unsigned long long)fnv);
			hash_reset(); for (size_t v = 0; v < m->num_vertices; v++) { ufbx_vec3 p = m->vertices.data[v]; hash_double(p.x); hash_double(p.y); hash_double(p.z); if (full) printf("  vertex %zu %016llx %016llx %016llx\n", v, (unsigned long long)bits(p.x), (unsigned long long)bits(p.y), (unsigned long long)bits(p.z)); }
			printf("  vertices hash=%016llx\n", (unsigned long long)fnv);
			dump_stream_vec3("position", &m->vertex_position, m->num_indices);
			dump_stream_vec3("normal", &m->vertex_normal, m->num_indices);
			for (size_t u = 0; u < m->uv_sets.count; u++) { char tag[64]; snprintf(tag, sizeof tag, "uv%zu", u); printf("  uvset %zu \"%s\"\n", u, m->uv_sets.data[u].name.data); dump_stream_vec2(tag, &m->uv_sets.data[u].vertex_uv, m->num_indices); }
			for (size_t c = 0; c < m->color_sets.count; c++) { char tag[64]; snprintf(tag, sizeof tag, "color%zu", c); printf("  colorset %zu \"%s\"\n", c, m->color_sets.data[c].name.data); dump_stream_vec4(tag, &m->color_sets.data[c].vertex_color, m->num_indices); }
			dump_stream_vec3("tangent", &m->vertex_tangent, m->num_indices);
			dump_stream_vec3("bitangent", &m->vertex_bitangent, m->num_indices);
			for (size_t k = 0; k < m->materials.count; k++) printf("  slot %zu material=%d\n", k, m->materials.data[k] ? (int)m->materials.data[k]->typed_id : -1);
		}
		for (size_t i = 0; i < scene->materials.count; i++) {
			ufbx_material *mat = scene->materials.data[i];
			printf("material %zu \"%s\" shader=%d shading=\"%s\"", i, mat->name.data, (int)mat->shader_type, mat->shading_model_name.data);
			print_vec3("diffuse", mat->fbx.diffuse_color.value_vec3);
			printf(" textures=%zu\n", mat->textures.count);
			for (size_t t = 0; t < mat->textures.count; t++) printf("  texture \"%s\" -> \"%s\"\n", mat->textures.data[t].material_prop.data, mat->textures.data[t].texture->relative_filename.data);
		}
		ufbx_free_scene(scene);
	}
	return failed;
}

// Windows narrow argv uses the system code page, while ufbx filenames and our dumps use UTF-8.
// Keep the original Unicode command line intact even outside the active ANSI code page.
int wmain(int argc, wchar_t **wide_argv) {
	char **argv = (char**)calloc((size_t)argc, sizeof(char*));
	if (!argv) return 3;
	int result = 3;
	for (int a = 0; a < argc; a++) {
		int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide_argv[a], -1, NULL, 0, NULL, NULL);
		if (!size || !(argv[a] = (char*)malloc((size_t)size))) goto cleanup;
		if (!WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide_argv[a], -1, argv[a], size, NULL, NULL)) goto cleanup;
	}
	result = dump_main(argc, argv);
cleanup:
	for (int a = 0; a < argc; a++) free(argv[a]);
	free(argv);
	return result;
}
