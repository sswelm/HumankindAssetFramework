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
// C99 fmin/fmax discard a single NaN operand. A ternary comparison propagates a right-hand NaN instead,
// changing ufbx's ear weights when finite tiny/large coordinates underflow/overflow intermediate products.
double ufbx_fmin(double a, double b) { return fmin(a, b); }
double ufbx_fmax(double a, double b) { return fmax(a, b); }
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
static uint32_t fbits(float f) { uint32_t u; memcpy(&u, &f, 4); return u; }
// thirteen sample times: 2.3 and 0.5 spans before, nine across (the ends included), 0.5 and 2.3 spans after; a zero span is a second
static void sample_times(double begin, double end, double *out) {
	double span = end - begin;
	if (!(span > 0.0)) span = 1.0;
	out[0] = begin - span * 2.3;
	out[1] = begin - span * 0.5;
	for (int k = 0; k <= 8; k++) out[2 + k] = begin + span * (double)k / 8.0;
	out[11] = end + span * 0.5;
	out[12] = end + span * 2.3;
}
static int anim_prop_cmp(const void *va, const void *vb) {
	const ufbx_anim_prop *a = (const ufbx_anim_prop*)va, *b = (const ufbx_anim_prop*)vb;
	if (a->element->element_id != b->element->element_id) return a->element->element_id < b->element->element_id ? -1 : 1;
	if (a->_internal_key != b->_internal_key) return a->_internal_key < b->_internal_key ? -1 : 1;
	int c = strcmp(a->prop_name.data, b->prop_name.data);
	if (c != 0) return c;
	if (a->anim_value->typed_id != b->anim_value->typed_id) return a->anim_value->typed_id < b->anim_value->typed_id ? -1 : 1;
	return 0;
}

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
		printf("settings up=%d front=%d right=%d unit=%016llx fps=%016llx version=%u exporter=%d exporter_version=%u ktime=%lld\n", (int)scene->settings.axes.up, (int)scene->settings.axes.front, (int)scene->settings.axes.right,
			(unsigned long long)bits(scene->settings.unit_meters), (unsigned long long)bits(scene->settings.frames_per_second), scene->metadata.version, (int)scene->metadata.exporter, scene->metadata.exporter_version, (long long)scene->metadata.ktime_second);
		printf("counts nodes=%zu meshes=%zu materials=%zu textures=%zu skins=%zu clusters=%zu bones=%zu poses=%zu anim_stacks=%zu anim_layers=%zu anim_values=%zu anim_curves=%zu\n",
			scene->nodes.count, scene->meshes.count, scene->materials.count, scene->textures.count, scene->skin_deformers.count, scene->skin_clusters.count, scene->bones.count, scene->poses.count, scene->anim_stacks.count,
			scene->anim_layers.count, scene->anim_values.count, scene->anim_curves.count);
		for (size_t i = 0; i < scene->nodes.count; i++) {
			ufbx_node *n = scene->nodes.data[i];
			printf("node %zu \"%s\" parent=%d attrib=%s mesh=%d inherit=%d order=%d rotspace=%d geomxf=%d visible=%d bind_pose=%d bone=%d\n", i, n->name.data, n->parent ? (int)n->parent->typed_id : -1,
				attrib_name(n->attrib_type), n->mesh ? (int)n->mesh->typed_id : -1, (int)n->inherit_mode, (int)n->rotation_order, n->use_rotation_space ? 1 : 0, n->has_geometry_transform ? 1 : 0, n->visible ? 1 : 0,
				n->bind_pose ? (int)n->bind_pose->typed_id : -1, n->bone ? (int)n->bone->typed_id : -1);
			printf("  local"); print_vec3("t", n->local_transform.translation); print_quat("r", n->local_transform.rotation); print_vec3("s", n->local_transform.scale); printf("\n");
			print_matrix("n2p", &n->node_to_parent);
			print_matrix("n2w", &n->node_to_world);
			if (n->has_geometry_transform) print_matrix("g2n", &n->geometry_to_node);
		}
		for (size_t i = 0; i < scene->meshes.count; i++) {
			ufbx_mesh *m = scene->meshes.data[i];
			printf("mesh %zu \"%s\" vertices=%zu faces=%zu indices=%zu triangles=%zu max_face=%zu materials=%zu instances=%zu\n", i, m->name.data, m->num_vertices, m->num_faces, m->num_indices, m->num_triangles, m->max_face_triangles, m->materials.count, m->instances.count);
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
			// step 3: every face through ufbx_triangulate_face (the quad rule, the n-gon ear clipping); corner indices in order
			{
				size_t cap = (size_t)m->max_face_triangles * 3, total = 0;
				uint32_t *tri = cap ? (uint32_t*)malloc(cap * sizeof(uint32_t)) : NULL;
				hash_reset();
				for (size_t f = 0; f < m->num_faces; f++) {
					uint32_t num = ufbx_triangulate_face(tri, cap, m, m->faces.data[f]);
					for (uint32_t k = 0; k < num * 3; k++) hash_u32(tri[k]);
					if (full) for (uint32_t t = 0; t < num; t++) printf("  tri %zu %u,%u,%u\n", f, tri[t*3], tri[t*3+1], tri[t*3+2]);
					total += num;
				}
				printf("  triangles count=%zu hash=%016llx\n", total, (unsigned long long)fnv);
				free(tri);
			}
			dump_stream_vec3("position", &m->vertex_position, m->num_indices);
			dump_stream_vec3("normal", &m->vertex_normal, m->num_indices);
			for (size_t u = 0; u < m->uv_sets.count; u++) { char tag[64]; snprintf(tag, sizeof tag, "uv%zu", u); printf("  uvset %zu \"%s\"\n", u, m->uv_sets.data[u].name.data); dump_stream_vec2(tag, &m->uv_sets.data[u].vertex_uv, m->num_indices); }
			for (size_t c = 0; c < m->color_sets.count; c++) { char tag[64]; snprintf(tag, sizeof tag, "color%zu", c); printf("  colorset %zu \"%s\"\n", c, m->color_sets.data[c].name.data); dump_stream_vec4(tag, &m->color_sets.data[c].vertex_color, m->num_indices); }
			dump_stream_vec3("tangent", &m->vertex_tangent, m->num_indices);
			dump_stream_vec3("bitangent", &m->vertex_bitangent, m->num_indices);
			for (size_t k = 0; k < m->materials.count; k++) printf("  slot %zu material=%d\n", k, m->materials.data[k] ? (int)m->materials.data[k]->typed_id : -1);
			for (size_t k = 0; k < m->skin_deformers.count; k++) printf("  skin %u\n", m->skin_deformers.data[k]->typed_id);
			for (size_t k = 0; k < m->instances.count; k++) printf("  instance %u\n", m->instances.data[k]->typed_id);
		}
		for (size_t i = 0; i < scene->materials.count; i++) {
			ufbx_material *mat = scene->materials.data[i];
			printf("material %zu \"%s\" shader=%d shading=\"%s\"", i, mat->name.data, (int)mat->shader_type, mat->shading_model_name.data);
			print_vec3("diffuse", mat->fbx.diffuse_color.value_vec3);
			printf(" textures=%zu\n", mat->textures.count);
			for (size_t t = 0; t < mat->textures.count; t++) printf("  texture \"%s\" -> \"%s\"\n", mat->textures.data[t].material_prop.data, mat->textures.data[t].texture->relative_filename.data);
		}
		// step 4: bones, skins (every vertex's sorted weights as a hash), clusters (raw weights as a hash, the four
		// matrices and the world transform as bits), poses (every bone's matrices as bits)
		for (size_t i = 0; i < scene->bones.count; i++) {
			ufbx_bone *b = scene->bones.data[i];
			printf("bone %zu \"%s\" root=%d radius=%016llx rel=%016llx instances=%zu\n", i, b->name.data, b->is_root ? 1 : 0, (unsigned long long)bits(b->radius), (unsigned long long)bits(b->relative_length), b->instances.count);
		}
		for (size_t i = 0; i < scene->skin_deformers.count; i++) {
			ufbx_skin_deformer *s = scene->skin_deformers.data[i];
			printf("skin %zu \"%s\" method=%d clusters=%zu vertices=%zu weights=%zu max_weights=%zu dq=%zu\n", i, s->name.data, (int)s->skinning_method, s->clusters.count, s->vertices.count, s->weights.count, s->max_weights_per_vertex, s->num_dq_weights);
			printf("  clusters"); for (size_t c = 0; c < s->clusters.count; c++) printf(" %u", s->clusters.data[c]->typed_id); printf("\n");
			hash_reset();
			for (size_t v = 0; v < s->vertices.count; v++) { ufbx_skin_vertex sv = s->vertices.data[v]; hash_u32(sv.weight_begin); hash_u32(sv.num_weights); hash_double(sv.dq_weight); if (full) printf("  sv %zu begin=%u n=%u dq=%016llx\n", v, sv.weight_begin, sv.num_weights, (unsigned long long)bits(sv.dq_weight)); }
			printf("  skin_vertices hash=%016llx\n", (unsigned long long)fnv);
			hash_reset();
			for (size_t w = 0; w < s->weights.count; w++) { ufbx_skin_weight sw = s->weights.data[w]; hash_u32(sw.cluster_index); hash_double(sw.weight); if (full) printf("  sw %zu cluster=%u w=%016llx\n", w, sw.cluster_index, (unsigned long long)bits(sw.weight)); }
			printf("  skin_weights hash=%016llx\n", (unsigned long long)fnv);
			hash_reset();
			for (size_t d = 0; d < s->num_dq_weights; d++) { hash_u32(s->dq_vertices.data[d]); hash_double(s->dq_weights.data[d]); }
			printf("  dq hash=%016llx\n", (unsigned long long)fnv);
		}
		for (size_t i = 0; i < scene->skin_clusters.count; i++) {
			ufbx_skin_cluster *c = scene->skin_clusters.data[i];
			printf("cluster %zu \"%s\" bone=%d num_weights=%zu\n", i, c->name.data, c->bone_node ? (int)c->bone_node->typed_id : -1, c->num_weights);
			hash_reset();
			for (size_t k = 0; k < c->num_weights; k++) { hash_u32(c->vertices.data[k]); hash_double(c->weights.data[k]); if (full) printf("  cw %zu %u %016llx\n", k, c->vertices.data[k], (unsigned long long)bits(c->weights.data[k])); }
			printf("  raw hash=%016llx\n", (unsigned long long)fnv);
			print_matrix("m2b", &c->mesh_node_to_bone); print_matrix("b2w", &c->bind_to_world); print_matrix("g2b", &c->geometry_to_bone); print_matrix("g2w", &c->geometry_to_world);
			printf("  g2wt"); print_vec3("t", c->geometry_to_world_transform.translation); print_quat("r", c->geometry_to_world_transform.rotation); print_vec3("s", c->geometry_to_world_transform.scale); printf("\n");
		}
		for (size_t i = 0; i < scene->poses.count; i++) {
			ufbx_pose *p = scene->poses.data[i];
			printf("pose %zu \"%s\" bind=%d bones=%zu\n", i, p->name.data, p->is_bind_pose ? 1 : 0, p->bone_poses.count);
			for (size_t b = 0; b < p->bone_poses.count; b++) { ufbx_bone_pose *bp = &p->bone_poses.data[b]; printf("  bone %u\n", bp->bone_node->typed_id); print_matrix("b2w", &bp->bone_to_world); print_matrix("b2p", &bp->bone_to_parent); }
		}
		// step 5: animation stacks, layers (their animated properties sorted by element id, key and name, as a hash), values,
		// curves (every keyframe with its float tangents as a hash; thirteen samples of ufbx_evaluate_curve across and beyond the
		// key range as a hash) and every node's transform through ufbx_evaluate_transform at thirteen samples of each stack
		for (size_t i = 0; i < scene->anim_stacks.count; i++) {
			ufbx_anim_stack *st = scene->anim_stacks.data[i];
			printf("anim_stack %zu \"%s\" begin=%016llx end=%016llx layers=", i, st->name.data, (unsigned long long)bits(st->time_begin), (unsigned long long)bits(st->time_end));
			for (size_t k = 0; k < st->layers.count; k++) printf("%s%u", k ? "," : "", st->layers.data[k]->typed_id);
			printf("\n");
		}
		for (size_t i = 0; i < scene->anim_layers.count; i++) {
			ufbx_anim_layer *l = scene->anim_layers.data[i];
			printf("anim_layer %zu \"%s\" weight=%016llx weight_anim=%d blended=%d additive=%d compose_rot=%d compose_scale=%d values=%zu props=%zu\n", i, l->name.data,
				(unsigned long long)bits(l->weight), l->weight_is_animated ? 1 : 0, l->blended ? 1 : 0, l->additive ? 1 : 0, l->compose_rotation ? 1 : 0, l->compose_scale ? 1 : 0, l->anim_values.count, l->anim_props.count);
			printf("  values"); for (size_t k = 0; k < l->anim_values.count; k++) printf(" %u", l->anim_values.data[k]->typed_id); printf("\n");
			// ufbx orders anim_props by element ADDRESS; the dump orders them by element id (the same order unless the
			// allocator's chunks are out of address order), key, name, then the value's id
			size_t n = l->anim_props.count;
			ufbx_anim_prop *sorted = n ? (ufbx_anim_prop*)malloc(n * sizeof(ufbx_anim_prop)) : NULL;
			for (size_t k = 0; k < n; k++) sorted[k] = l->anim_props.data[k];
			qsort(sorted, n, sizeof(ufbx_anim_prop), anim_prop_cmp);
			hash_reset();
			for (size_t k = 0; k < n; k++) {
				ufbx_anim_prop *ap = &sorted[k];
				hash_u32(ap->element->element_id); hash_bytes(ap->prop_name.data, ap->prop_name.length); hash_u32(ap->anim_value->typed_id);
				if (full) printf("  aprop %u \"%s\" value=%u\n", ap->element->element_id, ap->prop_name.data, ap->anim_value->typed_id);
			}
			printf("  aprops hash=%016llx\n", (unsigned long long)fnv);
			free(sorted);
		}
		for (size_t i = 0; i < scene->anim_values.count; i++) {
			ufbx_anim_value *v = scene->anim_values.data[i];
			printf("anim_value %zu \"%s\" default=%016llx,%016llx,%016llx curves=%d,%d,%d\n", i, v->name.data,
				(unsigned long long)bits(v->default_value.x), (unsigned long long)bits(v->default_value.y), (unsigned long long)bits(v->default_value.z),
				v->curves[0] ? (int)v->curves[0]->typed_id : -1, v->curves[1] ? (int)v->curves[1]->typed_id : -1, v->curves[2] ? (int)v->curves[2]->typed_id : -1);
		}
		for (size_t i = 0; i < scene->anim_curves.count; i++) {
			ufbx_anim_curve *c = scene->anim_curves.data[i];
			printf("anim_curve %zu \"%s\" keys=%zu pre=%d,%d post=%d,%d min=%016llx max=%016llx tmin=%016llx tmax=%016llx\n", i, c->name.data, c->keyframes.count,
				(int)c->pre_extrapolation.mode, c->pre_extrapolation.repeat_count, (int)c->post_extrapolation.mode, c->post_extrapolation.repeat_count,
				(unsigned long long)bits(c->min_value), (unsigned long long)bits(c->max_value), (unsigned long long)bits(c->min_time), (unsigned long long)bits(c->max_time));
			hash_reset();
			for (size_t k = 0; k < c->keyframes.count; k++) {
				ufbx_keyframe *key = &c->keyframes.data[k];
				hash_double(key->time); hash_double(key->value); hash_u32((uint32_t)key->interpolation);
				hash_u32(fbits(key->left.dx)); hash_u32(fbits(key->left.dy)); hash_u32(fbits(key->right.dx)); hash_u32(fbits(key->right.dy));
				if (full) printf("  key %zu t=%016llx v=%016llx i=%d l=%08x,%08x r=%08x,%08x\n", k, (unsigned long long)bits(key->time), (unsigned long long)bits(key->value), (int)key->interpolation,
					fbits(key->left.dx), fbits(key->left.dy), fbits(key->right.dx), fbits(key->right.dy));
			}
			printf("  keys hash=%016llx\n", (unsigned long long)fnv);
			double times[13]; sample_times(c->min_time, c->max_time, times);
			hash_reset();
			for (int k = 0; k < 13; k++) {
				double value = ufbx_evaluate_curve(c, times[k], 0.0);
				hash_double(value);
				if (full) printf("  sample %d t=%016llx v=%016llx\n", k, (unsigned long long)bits(times[k]), (unsigned long long)bits(value));
			}
			printf("  samples hash=%016llx\n", (unsigned long long)fnv);
		}
		for (size_t i = 0; i < scene->anim_stacks.count; i++) {
			ufbx_anim_stack *st = scene->anim_stacks.data[i];
			double times[13]; sample_times(st->time_begin, st->time_end, times);
			for (size_t j = 0; j < scene->nodes.count; j++) {
				ufbx_node *n = scene->nodes.data[j];
				hash_reset();
				for (int k = 0; k < 13; k++) {
					ufbx_transform t = ufbx_evaluate_transform(st->anim, n, times[k]);
					hash_double(t.translation.x); hash_double(t.translation.y); hash_double(t.translation.z);
					hash_double(t.rotation.x); hash_double(t.rotation.y); hash_double(t.rotation.z); hash_double(t.rotation.w);
					hash_double(t.scale.x); hash_double(t.scale.y); hash_double(t.scale.z);
					if (full) { printf("  at %d", k); print_vec3("t", t.translation); print_quat("r", t.rotation); print_vec3("s", t.scale); printf("\n"); }
				}
				printf("anim_eval stack=%zu node=%zu hash=%016llx\n", i, j, (unsigned long long)fnv);
			}
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
