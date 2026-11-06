<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { api, errorMessage } from '@/api/http'
import UiComboBox, { type ComboOption } from '@/components/UiComboBox.vue'
import UiModal from '@/components/UiModal.vue'
import { useAuthStore } from '@/stores/auth'
import { money, quantity } from '@/utils/money'
import type { SupplyLocation, SupplyStock, SurgeryPackage } from './types'

const auth = useAuthStore()
const canEdit = computed(() => auth.can('surgery.approve'))

const rows = ref<SurgeryPackage[]>([])
const services = ref<ComboOption[]>([])
const locations = ref<SupplyLocation[]>([])
const loading = ref(true)
const error = ref('')

interface Line {
  productId: number
  name: string
  unit: string
  quantity: number
}

const editor = reactive({
  open: false,
  editing: null as SurgeryPackage | null,
  name: '',
  serviceId: null as number | null,
  location: '',
  isActive: true,
  lines: [] as Line[],
  adding: null as number | null,
  saving: false,
  error: '',
})
const found = new Map<number, SupplyStock>()

async function load() {
  try {
    rows.value = await api<SurgeryPackage[]>('/surgery-packages?all=true')
    error.value = ''
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    loading.value = false
  }
}

function open(pkg: SurgeryPackage | null) {
  Object.assign(editor, {
    open: true,
    editing: pkg,
    name: pkg?.name ?? '',
    serviceId: pkg?.serviceId ?? null,
    location: pkg?.locationCode ?? locations.value.find((l) => l.code === 'surgery')?.code ?? locations.value[0]?.code ?? '',
    isActive: pkg?.isActive ?? true,
    lines: pkg?.items.map((i) => ({ productId: i.productId, name: i.productName, unit: i.unit, quantity: i.quantity })) ?? [],
    adding: null,
    saving: false,
    error: '',
  })
}

async function searchProducts(query: string, signal: AbortSignal): Promise<ComboOption[]> {
  if (!query || !editor.location) return []
  const found_ = await api<SupplyStock[]>(`/surgery-packages/stock?location=${encodeURIComponent(editor.location)}&q=${encodeURIComponent(query)}`, { signal })
  found_.forEach((r) => found.set(r.productId, r))
  return found_.map((r) => ({ value: r.productId, label: r.name, hint: `${r.code} · موجود: ${quantity(r.onHand)} ${r.unit}` }))
}

function onAdd(option: ComboOption | null) {
  if (!option) return
  const id = option.value as number
  if (!editor.lines.some((l) => l.productId === id)) editor.lines.push({ productId: id, name: option.label, unit: found.get(id)?.unit ?? '', quantity: 1 })
  editor.adding = null
}

async function save() {
  if (!editor.lines.length) {
    editor.error = 'حداقل یک جنس به پکیج اضافه کنید.'
    return
  }
  editor.saving = true
  editor.error = ''
  try {
    const body = {
      name: editor.name,
      serviceId: editor.serviceId,
      locationCode: editor.location,
      items: editor.lines.map((l) => ({ productId: l.productId, quantity: l.quantity })),
      isActive: editor.isActive,
      version: editor.editing?.version,
    }
    if (editor.editing) await api(`/surgery-packages/${editor.editing.id}`, { method: 'PUT', body })
    else await api('/surgery-packages', { method: 'POST', body })
    editor.open = false
    await load()
  } catch (cause) {
    editor.error = errorMessage(cause)
  } finally {
    editor.saving = false
  }
}

onMounted(async () => {
  try {
    const [loadedServices, loadedLocations] = await Promise.all([
      api<{ id: number; name: string; isActive: boolean; currentPrice: number | null }[]>('/services'),
      api<SupplyLocation[]>('/surgery-packages/locations'),
    ])
    services.value = loadedServices.filter((s) => s.isActive).map((s) => ({ value: s.id, label: s.name, hint: s.currentPrice === null ? 'بدون قیمت' : money(s.currentPrice) }))
    locations.value = loadedLocations
  } catch (cause) {
    error.value = errorMessage(cause)
  }
  await load()
})
</script>

<template>
  <div>
    <div class="d-flex flex-wrap align-items-start justify-content-between gap-2 mb-3">
      <p class="form-hint mb-0">هر پکیج یک نوع عملیات است با فیس آن و دوا و وسایلی که مصرف می‌کند. مریض فقط فیس پکیج را می‌پردازد و مواد آن پس از عملیات از گدام کسر می‌شود.</p>
      <button v-if="canEdit" type="button" class="btn btn-primary text-nowrap" @click="open(null)"><i class="ri-add-line" aria-hidden="true"></i> پکیج جدید</button>
    </div>
    <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
    <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>
    <div v-else-if="rows.length === 0" class="xr-state">هنوز پکیجی ساخته نشده است.</div>
    <div v-else class="xr-table-wrap">
      <table class="xr-table">
        <thead><tr><th>پکیج</th><th>فیس</th><th>اجناس</th><th>گدام</th><th>وضعیت</th><th v-if="canEdit"><span class="visually-hidden">عملیات</span></th></tr></thead>
        <tbody>
          <tr v-for="p in rows" :key="p.id">
            <td><strong>{{ p.name }}</strong><div class="xr-meta">{{ p.serviceName }}</div></td>
            <td>{{ p.price === null ? '—' : money(p.price) }}</td>
            <td>
              <div v-for="i in p.items" :key="i.productId" class="small">{{ i.productName }} × {{ quantity(i.quantity) }} {{ i.unit }}</div>
            </td>
            <td>{{ p.locationName }}</td>
            <td><span class="xr-badge" :class="p.isActive ? 'xr-badge--green' : 'xr-badge--red'">{{ p.isActive ? 'فعال' : 'غیرفعال' }}</span></td>
            <td v-if="canEdit"><button type="button" class="btn btn-soft btn-sm" @click="open(p)">ویرایش</button></td>
          </tr>
        </tbody>
      </table>
    </div>

    <UiModal v-if="editor.open" :title="editor.editing ? `ویرایش پکیج ${editor.editing.name}` : 'پکیج جدید عملیات'" @close="editor.open = false">
      <form @submit.prevent="save">
        <div class="mb-3">
          <label class="form-label" for="pk-name">نام پکیج</label>
          <input id="pk-name" v-model="editor.name" class="form-control" maxlength="200" required placeholder="مثلاً: عمل آب مروارید" />
        </div>
        <div class="row g-3 mb-3">
          <div class="col-md-7"><UiComboBox v-model="editor.serviceId" label="فیس (خدمت قابل پرداخت)" :options="services" :selected-label="editor.editing?.serviceName" required chip /></div>
          <div class="col-md-5">
            <label class="form-label" for="pk-location">گدام پیش‌فرض</label>
            <select id="pk-location" v-model="editor.location" class="form-select" required>
              <option v-for="l in locations" :key="l.code" :value="l.code">{{ l.name }}</option>
            </select>
          </div>
        </div>
        <p class="form-hint">قیمت فیس از «تعرفه خدمات» خوانده می‌شود. خدمت تازه را مسئول مالی می‌سازد.</p>

        <h3 class="h6">اجناس پکیج</h3>
        <div v-if="editor.lines.length" class="xr-table-wrap mb-2">
          <table class="xr-table">
            <thead><tr><th>جنس</th><th>مقدار</th><th><span class="visually-hidden">حذف</span></th></tr></thead>
            <tbody>
              <tr v-for="(line, index) in editor.lines" :key="line.productId">
                <td>{{ line.name }} <span class="xr-meta">{{ line.unit }}</span></td>
                <td><input v-model.number="line.quantity" class="form-control form-control-sm pk-qty" type="number" min="0.001" step="any" required :aria-label="`مقدار ${line.name}`" /></td>
                <td><button type="button" class="btn btn-quiet btn-sm text-danger" :aria-label="`حذف ${line.name}`" @click="editor.lines.splice(index, 1)"><i class="ri-delete-bin-line" aria-hidden="true"></i></button></td>
              </tr>
            </tbody>
          </table>
        </div>
        <div class="mb-3"><UiComboBox v-model="editor.adding" label="افزودن جنس" :search="searchProducts" placeholder="نام دوا یا وسیله را تایپ کنید" @select="onAdd" /></div>

        <div class="form-check mb-3">
          <input id="pk-active" v-model="editor.isActive" class="form-check-input" type="checkbox" />
          <label class="form-check-label" for="pk-active">فعال (داکتر می‌تواند انتخاب کند)</label>
        </div>

        <div v-if="editor.error" class="alert alert-danger" role="alert">{{ editor.error }}</div>
        <div class="xr-form-actions">
          <button type="submit" class="btn btn-primary" :disabled="editor.saving">{{ editor.saving ? 'در حال ذخیره…' : 'ذخیره پکیج' }}</button>
          <button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button>
        </div>
      </form>
    </UiModal>
  </div>
</template>

<style scoped>
.pk-qty {
  width: 110px;
}
</style>
