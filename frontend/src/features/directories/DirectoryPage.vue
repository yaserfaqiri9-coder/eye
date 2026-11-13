<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { api, errorMessage } from '@/api/http'
import { clearLookups } from '@/api/lookups'
import { useAuthStore } from '@/stores/auth'
import UiCard from '@/components/UiCard.vue'
import UiModal from '@/components/UiModal.vue'
import { removeRecord } from '@/utils/removeRecord'
const props = defineProps<{ kind: 'departments' | 'suppliers' }>()
interface Row { id: number; name: string; code?: string; phone?: string; address?: string; isActive: boolean; version: number }
const auth = useAuthStore()
const department = computed(() => props.kind === 'departments')
const title = computed(() => department.value ? 'مدیریت بخش‌ها' : 'تأمین‌کنندگان')
const canEdit = computed(() => auth.can(department.value ? 'settings.edit' : 'inventory.create'))
const rows = ref<Row[]>([])
const loading = ref(false)
const error = ref('')
const notice = ref('')
const form = reactive({ open: false, id: null as number | null, version: null as number | null, name: '', code: '', phone: '', address: '', isActive: true, saving: false, error: '' })
let loadSequence = 0
async function load() {
  const sequence = ++loadSequence
  rows.value = []
  loading.value = true; error.value = ''
  try {
    const result = await api<Row[]>(`/${props.kind}`)
    if (sequence === loadSequence) rows.value = result
  } catch (e) {
    if (sequence === loadSequence) error.value = errorMessage(e)
  } finally {
    if (sequence === loadSequence) loading.value = false
  }
}
function open(row?: Row) {
  Object.assign(form, { open: true, id: row?.id ?? null, version: row?.version ?? null, name: row?.name ?? '', code: row?.code ?? '', phone: row?.phone ?? '', address: row?.address ?? '', isActive: row?.isActive ?? true, saving: false, error: '' })
}
async function save() {
  form.saving = true; form.error = ''
  try {
    await api(`/${props.kind}${form.id ? `/${form.id}` : ''}`, { method: form.id ? 'PUT' : 'POST', body: { name: form.name, phone: form.phone, address: form.address, isActive: form.isActive, version: form.version } })
    clearLookups(); form.open = false; await load()
  } catch (e) { form.error = errorMessage(e) } finally { form.saving = false }
}
async function remove(row: Row) {
  error.value = ''; notice.value = ''
  try {
    notice.value = await removeRecord(`/${props.kind}/${row.id}?version=${row.version}`, row.name)
    if (notice.value) { clearLookups(); await load() }
  } catch (e) { error.value = errorMessage(e) }
}
watch(() => props.kind, () => { form.open = false; notice.value = ''; void load() })
onMounted(load)
</script>
<template>
  <UiCard :title="title">
    <template #actions><button v-if="canEdit" class="btn btn-primary" @click="open()">{{ department ? 'بخش جدید' : 'تأمین‌کننده جدید' }}</button></template>
    <p v-if="department" class="text-muted">بخش‌های فعال در پذیرش و ثبت نوبت نمایش داده می‌شوند. برای حفظ سوابق، بخش را غیرفعال کنید.</p>
    <div v-if="error" class="alert alert-danger" role="alert">{{ error }}</div>
    <div v-if="notice" class="alert alert-success" role="status">{{ notice }}</div>
    <div v-if="loading" class="xr-state" role="status">در حال بارگذاری...</div>
    <div v-else-if="!rows.length" class="xr-state">{{ department ? 'بخشی ثبت نشده است. با «بخش جدید» یک بخش فعال بسازید.' : 'تأمین‌کننده‌ای ثبت نشده است. ابتدا تأمین‌کننده جدید بسازید، سپس در خرید انتخاب کنید.' }}</div>
    <div v-else class="xr-table-wrap"><table class="xr-table">
      <thead><tr><th>نام</th><th v-if="department">کد</th><template v-else><th>تماس</th><th>آدرس</th></template><th>وضعیت</th><th v-if="canEdit">عملیات</th></tr></thead>
      <tbody><tr v-for="row in rows" :key="row.id"><td>{{ row.name }}</td><td v-if="department" dir="ltr">{{ row.code }}</td><template v-else><td dir="ltr">{{ row.phone || '—' }}</td><td>{{ row.address || '—' }}</td></template><td><span class="xr-badge" :class="row.isActive ? 'xr-badge--green' : 'xr-badge--red'">{{ row.isActive ? 'فعال' : 'غیرفعال' }}</span></td><td v-if="canEdit" class="text-nowrap"><button class="btn btn-soft btn-sm" @click="open(row)">ویرایش</button><button class="btn btn-soft-danger btn-sm ms-2" @click="remove(row)">حذف</button></td></tr></tbody>
    </table></div>
  </UiCard>
  <UiModal v-if="form.open" :title="form.id ? 'ویرایش' : department ? 'بخش جدید' : 'تأمین‌کننده جدید'" @close="form.open = false">
    <form @submit.prevent="save">
      <div class="mb-3"><label for="directory-name" class="form-label">نام</label><input id="directory-name" v-model="form.name" class="form-control" :maxlength="department ? 100 : 150" required /></div>
      <div v-if="department" class="mb-3"><label for="directory-code" class="form-label">کد بخش</label><input id="directory-code" :value="form.id ? form.code : 'خودکار'" class="form-control" dir="ltr" readonly tabindex="-1" /><small class="text-muted">کد را سیستم می‌دهد و تغییر نمی‌کند.</small></div>
      <template v-else>
        <div class="mb-3"><label for="directory-phone" class="form-label">تماس</label><input id="directory-phone" v-model="form.phone" class="form-control" dir="ltr" maxlength="30" /></div>
        <div class="mb-3"><label for="directory-address" class="form-label">آدرس</label><textarea id="directory-address" v-model="form.address" class="form-control" maxlength="300"></textarea></div>
      </template>
      <label class="form-check-label mb-3"><input v-model="form.isActive" type="checkbox" class="form-check-input ms-2" />فعال</label>
      <div v-if="form.error" class="alert alert-danger" role="alert">{{ form.error }}</div>
      <div class="xr-form-actions"><button type="submit" class="btn btn-primary" :disabled="form.saving">ذخیره</button><button type="button" class="btn btn-soft-danger" data-modal-cancel>انصراف</button></div>
    </form>
  </UiModal>
</template>
