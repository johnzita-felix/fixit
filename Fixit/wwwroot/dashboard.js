(() => {
    const body = document.body;
    const role = body.dataset.role;
    const page = body.dataset.page;
    const isAdmin = role === 'admin';
    const statusOptions = ['Pending', 'Acknowledged', 'In Progress', 'Resolved', 'Rejected'];
    const priorities = ['Low', 'Normal', 'High', 'Critical'];
    let session;
    let profile;
    let classes = [];
    let reports = [];
    let notifications = [];
    let activeReportId = null;
    let activeGradeId = null;
    let activeFolder = 'overview';
    let overviewPage = 1;
    let gradeReportsPage = 1;
    let adminRefreshTimer = null;
    let classFolderDialogState = null;

    const byId = id => document.getElementById(id);
    const escapeHtml = value => String(value ?? '').replace(/[&<>"']/g, character => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    })[character]);

    async function request(url, options = {}) {
        const response = await fetch(url, {
            credentials: 'same-origin',
            headers: {
                Accept: 'application/json',
                ...(options.body && !(options.body instanceof FormData)
                    ? { 'Content-Type': 'application/json' }
                    : {}),
                ...options.headers
            },
            ...options
        });
        const payload = await response.json().catch(() => ({}));
        if (response.status === 401) {
            window.location.replace('index.html');
            throw new Error('Your session has expired. Sign in again.');
        }
        if (!response.ok) {
            const details = Array.isArray(payload.errors) ? payload.errors.slice(0, 20).join('\n') : '';
            throw new Error([payload.message || payload.detail, details]
                .filter(Boolean).join('\n') || `The request failed (HTTP ${response.status}).`);
        }
        return payload;
    }

    function showMessage(text, isError = false) {
        const message = byId(isAdmin ? 'admin-global-message' : 'dashboard-message');
        if (!message) return;
        message.textContent = text;
        message.classList.toggle('is-error', isError);
        message.hidden = !text;
    }

    function formatDate(value, withTime = false) {
        const date = new Date(value);
        if (Number.isNaN(date.getTime())) return 'Date unavailable';
        return new Intl.DateTimeFormat(undefined, withTime
            ? { month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit' }
            : { month: 'short', day: 'numeric', year: 'numeric' }).format(date);
    }

    function statusSlug(status) {
        return String(status || '').toLowerCase().replaceAll(' ', '-').replace(/[^a-z0-9-]/g, '');
    }

    function statusPill(status) {
        return `<span class="status-pill status-${statusSlug(status)}">${escapeHtml(status || 'Pending')}</span>`;
    }

    function priorityPill(priority) {
        const value = priorities.includes(priority) ? priority : 'Normal';
        return `<span class="priority-pill priority-${value.toLowerCase()}">${escapeHtml(value)}</span>`;
    }

    function reviewLabel(isReviewed) {
        return isReviewed
            ? '<span class="review-state is-reviewed">Reviewed</span>'
            : '<span class="review-state is-unreviewed"><span aria-hidden="true"></span>New · Unreviewed</span>';
    }

    function reviewAction(report) {
        return report.isReviewed
            ? reviewLabel(true)
            : `<button class="review-action" type="button" data-mark-reviewed="${report.id}">Mark reviewed</button>`;
    }

    function updateIdentity() {
        const name = session?.username || 'Student';
        if (byId('profile-name')) byId('profile-name').textContent = name;
        if (byId('menu-name')) byId('menu-name').textContent = name;
        if (byId('profile-avatar')) byId('profile-avatar').textContent = name.trim().charAt(0).toUpperCase() || 'S';
        if (byId('settings-name')) byId('settings-name').value = name;
        const heading = byId('welcome-heading');
        if (heading) heading.textContent = `Welcome back, ${name.split(/\s+/)[0]}.`;
    }

    function updateClassStatus() {
        const element = byId('class-status');
        if (!element || !profile) return;
        const hasClass = profile.gradeLevelId && profile.sectionId;
        element.textContent = hasClass
            ? `Class on file · ${profile.gradeLevel || 'Unavailable grade'} · ${profile.section || 'Unavailable section'}`
            : 'Class details are not set. Reports can still be submitted; add your class in Settings when ready.';
        element.classList.toggle('is-assigned', Boolean(hasClass));
        element.hidden = false;
    }

    function setSettingsSections(gradeId, selectedSectionId = '') {
        const select = byId('settings-section');
        if (!select) return;
        const grade = classes.find(item => String(item.id) === String(gradeId));
        const sections = grade?.sections || [];
        select.innerHTML = grade
            ? `<option value="">Not set</option>${sections.map(item => `<option value="${item.id}">${escapeHtml(item.name)}</option>`).join('')}`
            : '<option value="">Not set</option>';
        select.disabled = !grade || sections.length === 0;
        if (sections.some(item => String(item.id) === String(selectedSectionId)))
            select.value = String(selectedSectionId);
    }

    function populateClassSettings() {
        const gradeSelect = byId('settings-grade');
        const status = byId('class-settings-status');
        if (!gradeSelect || !status || !profile) return;
        gradeSelect.innerHTML = `<option value="">Not set</option>${classes.map(item =>
            `<option value="${item.id}">${escapeHtml(item.name)}</option>`).join('')}`;
        const grade = classes.find(item => item.id === profile.gradeLevelId);
        const section = grade?.sections.find(item => item.id === profile.sectionId);
        if (grade && section) {
            gradeSelect.value = String(grade.id);
            setSettingsSections(grade.id, section.id);
            status.textContent = `Current class: ${grade.name} · ${section.name}. You can change or clear it here.`;
        } else if (profile.gradeLevelId || profile.sectionId) {
            setSettingsSections('');
            status.textContent = `Saved class assignment: ${profile.gradeLevel || 'Grade not set'} · ${profile.section || 'Section not set'}. This option is no longer active; it remains on your account until you save a new choice or clear it.`;
        } else {
            setSettingsSections('');
            status.textContent = 'Class information is incomplete. You can set it now or leave both fields blank. Reports are not blocked.';
        }
    }

    function getStudentReports() {
        const query = byId('report-search')?.value.trim().toLowerCase() || '';
        const status = byId('status-filter')?.value || '';
        const category = byId('category-filter')?.value || '';
        const sort = byId('sort-filter')?.value || 'newest';
        return reports
            .filter(report => !status || report.status === status)
            .filter(report => !category || report.category === category)
            .filter(report => !query || [report.title, report.location, report.category, report.description]
                .some(value => String(value || '').toLowerCase().includes(query)))
            .sort((left, right) => sort === 'oldest'
                ? new Date(left.createdAt) - new Date(right.createdAt)
                : new Date(right.createdAt) - new Date(left.createdAt));
    }

    function reportRow(report, compact = false) {
        return `<button type="button" class="report-row" data-action="open-report" data-report-id="${report.id}" aria-label="View report: ${escapeHtml(report.title)}">
            <span class="report-row-main">
                <span class="report-row-title">${escapeHtml(report.title)}</span>
                <span class="report-row-meta">${escapeHtml(report.category || 'General')} · ${escapeHtml(report.location)}${report.hasPhoto ? ' · Photo attached' : ''}</span>
            </span>
            <span class="report-row-side">${statusPill(report.status)}${compact ? `<span class="report-row-date">${formatDate(report.createdAt)}</span>` : '<span class="report-arrow" aria-hidden="true">→</span>'}</span>
        </button>`;
    }

    function emptyState(title, text) {
        return `<div class="empty-state"><strong>${escapeHtml(title)}</strong><span>${escapeHtml(text)}</span></div>`;
    }

    function renderStudentReports() {
        const counts = {
            total: reports.length,
            pending: reports.filter(item => ['Pending', 'Acknowledged'].includes(item.status)).length,
            active: reports.filter(item => item.status === 'In Progress').length,
            resolved: reports.filter(item => item.status === 'Resolved').length
        };
        for (const [key, value] of Object.entries(counts)) {
            const element = byId(`stat-${key}`);
            if (element) element.textContent = value;
        }
        const recent = byId('recent-reports');
        if (recent) {
            recent.innerHTML = reports.length
                ? reports.slice(0, 4).map(report => reportRow(report, true)).join('')
                : emptyState('No reports yet', 'Use the + button to submit a maintenance report, then follow its progress here.');
        }
        const list = byId('report-grid');
        if (list) {
            const visible = getStudentReports();
            if (byId('visible-report-count'))
                byId('visible-report-count').textContent = `${visible.length} ${visible.length === 1 ? 'report' : 'reports'}`;
            list.innerHTML = visible.length
                ? visible.map(report => reportRow(report)).join('')
                : reports.length
                    ? emptyState('No reports match these filters', 'Try another status or search term.')
                    : emptyState('No reports yet', 'Use the + button to submit a report. Your submissions stay private to your account.');
        }
    }

    function renderNotifications() {
        const list = byId('student-updates');
        if (!list) return;
        const relevant = notifications.filter(item => reports.some(report => report.id === item.reportId));
        list.innerHTML = relevant.length
            ? relevant.slice(0, 5).map(item => `<button class="update-item" type="button" data-action="open-notification" data-notification-id="${item.id}" data-report-id="${item.reportId}">
                <strong>${escapeHtml(item.message)}</strong>
                <time>${formatDate(item.createdAt, true)}</time>
            </button>`).join('')
            : '<p class="empty-state">There are no updates yet. When the maintenance team changes one of your reports, the update will appear here.</p>';
    }

    async function openReport(reportId) {
        const dialog = byId('report-details-dialog');
        const content = byId(isAdmin ? 'admin-report-details' : 'report-details-body');
        if (!dialog || !content) return;
        content.innerHTML = '<p class="loading-state">Loading report details…</p>';
        if (isAdmin) {
            byId('admin-report-form').hidden = true;
            byId('admin-report-error').textContent = '';
        }
        dialog.showModal();
        try {
            const report = await request(`/api/reports/${reportId}`);
            activeReportId = report.id;
            const history = (report.history || []).map(entry => `<li>
                <span class="timeline-dot" aria-hidden="true"></span>
                <div><strong>${escapeHtml(entry.action)}</strong><p>${escapeHtml(entry.details)}</p>
                <time>${formatDate(entry.createdAt, true)}${entry.actor ? ` · ${escapeHtml(entry.actor)}` : ''}</time></div>
            </li>`).join('');
            content.innerHTML = `<p class="detail-kicker">REPORT FI-${String(report.id).padStart(6, '0')}</p>
                <h2 id="details-title">${escapeHtml(report.title)}</h2>
                <div class="detail-meta">${statusPill(report.status)}<span>${escapeHtml(report.category)}</span><span>${escapeHtml(report.location)}</span><span>Submitted ${formatDate(report.createdAt)}</span></div>
                ${isAdmin ? `<section class="detail-block"><h3>Submitted by</h3><p>${escapeHtml(report.reporter || 'Unknown account')} · ${escapeHtml(report.gradeLevel || 'Grade not set')} · ${escapeHtml(report.section || 'Section not set')}</p></section>` : ''}
                <section class="detail-block"><h3>Description</h3><p>${escapeHtml(report.description)}</p></section>
                ${report.publicUpdate ? `<section class="detail-block"><h3>Latest team update</h3><p>${escapeHtml(report.publicUpdate)}</p></section>` : ''}
                ${report.adminNotes && isAdmin ? `<section class="detail-block"><h3>Internal note</h3><p>${escapeHtml(report.adminNotes)}</p></section>` : ''}
                ${report.assignee && isAdmin ? `<section class="detail-block"><h3>Assigned to</h3><p>${escapeHtml(report.assignee)}</p></section>` : ''}
                ${report.photo ? `<section class="detail-block"><h3>Attached photo</h3><img class="detail-photo" src="${escapeHtml(report.photo)}" alt="Photo attached to ${escapeHtml(report.title)}"></section>` : ''}
                <section class="detail-block"><h3>Progress history</h3>${history
                    ? `<ol class="report-timeline">${history}</ol>`
                    : '<p>No status changes have been recorded yet.</p>'}</section>`;
            if (isAdmin) {
                byId('admin-status').value = report.status;
                byId('admin-priority').value = report.priority || 'Normal';
                byId('admin-assignee').value = report.assignee || '';
                byId('admin-public-update').value = report.publicUpdate || '';
                byId('admin-notes').value = report.adminNotes || '';
                byId('admin-report-form').hidden = false;
            }
        } catch (error) {
            content.innerHTML = `<p class="field-error" role="alert">${escapeHtml(error.message)}</p>`;
        }
    }

    async function loadStudentNotifications() {
        try {
            notifications = await request('/api/notifications');
            renderNotifications();
        } catch (error) {
            const list = byId('student-updates');
            if (list) list.innerHTML = `<p class="field-error" role="alert">${escapeHtml(error.message)}</p>`;
        }
    }

    async function loadStudent() {
        [profile, reports] = await Promise.all([request('/api/profile'), request('/api/reports')]);
        classes = profile.grades || [];
        profile = profile.profile;
        updateIdentity();
        updateClassStatus();
        populateClassSettings();
        renderStudentReports();
        await loadStudentNotifications();
    }

    function adminBreadcrumbs() {
        const nav = byId('folder-breadcrumbs');
        if (!nav) return;
        const parts = ['<button type="button" data-folder-level="overview">Overview</button>'];
        if (activeGradeId) {
            const grade = classes.find(item => item.id === activeGradeId);
            parts.push('<span aria-hidden="true">/</span>');
            parts.push(`<span aria-current="page">${escapeHtml(grade?.name || 'Grade level')}</span>`);
        }
        nav.innerHTML = parts.join('');
    }

    function folderMenu(type, id, name, isActive) {
        return `<button class="folder-menu-trigger" type="button" aria-label="Actions for ${escapeHtml(name)}" aria-expanded="false" aria-haspopup="true"><span aria-hidden="true">•••</span></button>
            <div class="folder-action-menu" hidden>
                <button type="button" data-folder-manage="rename" data-folder-type="${type}" data-folder-id="${id}" data-folder-name="${escapeHtml(name)}">Rename</button>
                <button type="button" data-folder-manage="delete" data-folder-type="${type}" data-folder-id="${id}" data-folder-name="${escapeHtml(name)}">Delete</button>
                <button type="button" data-folder-manage="${isActive ? 'archive' : 'restore'}" data-folder-type="${type}" data-folder-id="${id}" data-folder-name="${escapeHtml(name)}">${isActive ? 'Archive' : 'Restore'}</button>
            </div>`;
    }

    function renderAdminReportTable(items, includeGrade = false) {
        if (!items.length)
            return emptyState('No reports match these filters', 'Try changing the search or filters to see more reports.');
        return `<div class="admin-report-table-scroll"><table class="admin-report-list"><thead><tr>
            <th>Report</th>${includeGrade ? '<th>Grade Level</th>' : ''}<th>Category</th><th>Section</th>
            <th>Submitted</th><th>Priority</th><th>Status</th><th>Review</th></tr></thead><tbody>
            ${items.map(report => `<tr class="${report.isReviewed ? '' : 'is-new-report'}">
                <td><button type="button" class="report-title-link" data-action="open-report" data-report-id="${report.id}">
                    <span>FI-${String(report.id).padStart(6, '0')} · ${escapeHtml(report.title)}</span>
                    ${report.description ? `<small>${escapeHtml(report.description)}</small>` : ''}
                </button></td>
                ${includeGrade ? `<td>${escapeHtml(report.gradeLevel || 'Unassigned')}</td>` : ''}
                <td>${escapeHtml(report.category)}</td><td>${escapeHtml(report.section || '—')}</td>
                <td>${formatDate(report.createdAt)}</td><td>${priorityPill(report.priority)}</td>
                <td>${statusPill(report.status)}</td><td>${reviewAction(report)}</td></tr>`).join('')}
        </tbody></table></div>`;
    }

    function folderEndpoint(type, id, action) {
        return type === 'grade'
            ? `/api/admin/class-options/grades/${id}${action === 'status' ? '/status' : action === 'check' ? '/deletion-check' : ''}`
            : `/api/admin/class-options/sections/${id}${action === 'status' ? '/status' : action === 'check' ? '/deletion-check' : ''}`;
    }

    function openClassFolderDialog(state) {
        classFolderDialogState = state;
        const dialog = byId('class-folder-dialog');
        const typeName = state.type === 'grade' ? 'grade level' : 'section';
        const parent = state.type === 'section'
            ? classes.find(grade => grade.sections.some(section => section.id === state.id))
            : null;
        const location = state.type === 'section' ? ` in ${parent?.name || 'its grade level'}` : '';
        const parentField = byId('class-folder-parent-field');
        const nameField = byId('class-folder-name-field');
        const confirmField = byId('class-folder-confirm-field');
        const nameInput = byId('class-folder-name');
        const confirmInput = byId('class-folder-confirm-name');
        const submit = byId('class-folder-submit');
        const mode = state.mode;

        byId('class-folder-error').textContent = '';
        byId('class-folder-dialog-title').textContent = mode === 'create-grade' ? 'Create grade level'
            : mode === 'create-section' ? 'Create section'
                : mode === 'rename' ? `Rename ${typeName}`
                    : mode === 'delete' ? `Delete ${typeName} permanently`
                        : mode === 'archive' ? `Archive ${typeName}`
                            : `Restore ${typeName}`;
        byId('class-folder-dialog-eyebrow').textContent = mode.startsWith('create') ? 'NEW CLASS FOLDER' : 'FOLDER MANAGEMENT';
        parentField.hidden = mode !== 'create-section';
        nameField.hidden = !['create-grade', 'create-section', 'rename'].includes(mode);
        confirmField.hidden = mode !== 'delete';
        nameInput.value = mode === 'rename' ? state.name : '';
        nameInput.maxLength = state.type === 'grade' ? 40 : 80;
        confirmInput.value = '';
        byId('class-folder-required-name').textContent = state.name || '';
        if (mode === 'create-section') {
            const activeGrades = classes.filter(grade => grade.isActive);
            byId('class-folder-parent').innerHTML = activeGrades.map(grade =>
                `<option value="${grade.id}">${escapeHtml(grade.name)}</option>`).join('');
            if (state.gradeId) byId('class-folder-parent').value = String(state.gradeId);
        }

        if (mode === 'create-grade') {
            byId('class-folder-dialog-copy').textContent = 'Create a grade-level folder. It will be available for student assignments as soon as it has an active section.';
            submit.textContent = 'Create grade level';
        } else if (mode === 'create-section') {
            byId('class-folder-dialog-copy').textContent = 'Create a section under an active grade level. Students can select it after it is saved.';
            submit.textContent = 'Create section';
        } else if (mode === 'rename') {
            byId('class-folder-dialog-copy').textContent = `Rename “${state.name}”${location}. Existing student assignments and historical reports remain linked to this folder.`;
            submit.textContent = 'Save name';
        } else if (mode === 'delete') {
            byId('class-folder-dialog-copy').textContent = `Permanently delete “${state.name}”${location}. This is only allowed when no student or report history depends on it. No students or reports will be deleted.`;
            submit.textContent = 'Delete permanently';
        } else if (mode === 'archive') {
            const dependencies = state.dependencies || {};
            const countText = (count, noun) => `${count || 0} ${noun}${count === 1 ? '' : 's'}`;
            const counts = state.type === 'grade'
                ? `${countText(dependencies.sectionCount, 'section')}, ${countText(dependencies.studentCount, 'student')}, and ${countText(dependencies.reportCount, 'report')} are associated.`
                : `${countText(dependencies.studentCount, 'student')} and ${countText(dependencies.reportCount, 'report')} are associated.`;
            byId('class-folder-dialog-copy').textContent = `Archive “${state.name}”${location}. ${counts} Archiving hides this option from student dropdowns and prevents new assignments; existing assignments and historical reports remain intact and accessible to administrators.`;
            submit.textContent = 'Archive folder';
        } else {
            const gradeActive = state.type !== 'section' || parent?.isActive;
            byId('class-folder-dialog-copy').textContent = gradeActive
                ? `Restore “${state.name}”${location}. It will again be available for new student assignments. Historical student and report records remain unchanged.`
                : `Restore “${state.name}”${location}. Its parent grade level is archived, so students will not see it in dropdowns until that grade level is restored. Historical records remain unchanged.`;
            submit.textContent = 'Restore folder';
        }
        dialog.showModal();
        if (mode === 'rename') nameInput.focus();
        else if (mode === 'delete') confirmInput.focus();
        else if (nameField.hidden) submit.focus();
        else nameInput.focus();
    }

    async function prepareFolderManagement(button) {
        const type = button.dataset.folderType;
        const id = Number(button.dataset.folderId);
        const name = button.dataset.folderName;
        const mode = button.dataset.folderManage;
        if (mode === 'rename') {
            openClassFolderDialog({ mode, type, id, name });
            return;
        }
        if (mode === 'archive' || mode === 'delete') {
            const dependencies = await request(folderEndpoint(type, id, 'check'));
            openClassFolderDialog({
                mode: mode === 'delete' && dependencies.canDelete ? 'delete' : 'archive',
                type, id, name, dependencies
            });
            return;
        }
        openClassFolderDialog({ mode: 'restore', type, id, name });
    }

    async function submitClassFolderDialog(event) {
        event.preventDefault();
        const state = classFolderDialogState;
        if (!state) return;
        const submit = byId('class-folder-submit');
        const error = byId('class-folder-error');
        submit.disabled = true;
        error.textContent = '';
        try {
            let result;
            if (state.mode === 'create-grade') {
                result = await request('/api/admin/class-options/grades', {
                    method: 'POST',
                    body: JSON.stringify({ name: byId('class-folder-name').value })
                });
            } else if (state.mode === 'create-section') {
                const gradeId = Number(byId('class-folder-parent').value);
                result = await request(`/api/admin/class-options/grades/${gradeId}/sections`, {
                    method: 'POST',
                    body: JSON.stringify({ name: byId('class-folder-name').value })
                });
            } else if (state.mode === 'rename') {
                result = await request(folderEndpoint(state.type, state.id, ''), {
                    method: 'PUT',
                    body: JSON.stringify({ name: byId('class-folder-name').value })
                });
            } else if (state.mode === 'delete') {
                if (byId('class-folder-confirm-name').value !== state.name)
                    throw new Error('Type the folder name exactly to confirm permanent deletion.');
                result = await request(folderEndpoint(state.type, state.id, ''), { method: 'DELETE' });
            } else {
                const isActive = state.mode === 'restore';
                result = await request(folderEndpoint(state.type, state.id, 'status'), {
                    method: 'PATCH',
                    body: JSON.stringify({ isActive })
                });
            }
            byId('class-folder-dialog').close();
            classFolderDialogState = null;
            showMessage(result.message);
            await loadClassDirectory();
            if (activeFolder === 'reports') await loadGradeReports();
            await loadAdminOverview();
        } catch (requestError) {
            error.textContent = requestError.message;
        } finally {
            submit.disabled = false;
        }
    }

    async function loadGradeReports() {
        if (!activeGradeId) return;
        activeFolder = 'reports';
        adminBreadcrumbs();
        byId('folder-toolbar').hidden = false;
        byId('folder-content').innerHTML = '<p class="loading-state">Loading reports…</p>';
        const params = new URLSearchParams({
            search: byId('folder-search').value.trim(),
            status: byId('folder-status').value,
            category: byId('folder-category').value,
            sectionId: byId('folder-section').value,
            sort: byId('folder-sort').value,
            page: String(gradeReportsPage)
        });
        const result = await request(`/api/admin/report-folders/grades/${activeGradeId}/reports?${params}`);
        const sectionSelect = byId('folder-section');
        const selectedSection = result.sections.some(section => String(section.id) === sectionSelect.value)
            ? sectionSelect.value : '';
        sectionSelect.innerHTML = `<option value="">All sections</option>${result.sections.map(section =>
            `<option value="${section.id}">${escapeHtml(section.name)}</option>`).join('')}`;
        sectionSelect.value = selectedSection;
        if (!selectedSection && params.get('sectionId')) {
            gradeReportsPage = 1;
            return loadGradeReports();
        }
        const firstItem = result.totalReports ? (result.page - 1) * result.pageSize + 1 : 0;
        const lastItem = Math.min(result.page * result.pageSize, result.totalReports);
        const pagination = result.totalReports
            ? `<div class="table-pagination"><span>Showing ${firstItem}–${lastItem} of ${result.totalReports}</span>
                <div><button type="button" data-grade-page="${result.page - 1}" ${result.page <= 1 ? 'disabled' : ''}>Previous</button>
                <span>Page ${result.page} of ${result.totalPages}</span>
                <button type="button" data-grade-page="${result.page + 1}" ${result.page >= result.totalPages ? 'disabled' : ''}>Next</button></div></div>`
            : '';
        byId('folder-content').innerHTML = `<div class="folder-section-heading"><div><h2>${escapeHtml(result.gradeLevel)} reports</h2>
            <span class="record-count">${result.totalReports} ${result.totalReports === 1 ? 'report' : 'reports'} · all sections</span></div></div>
            ${renderAdminReportTable(result.reports)}${pagination}`;
    }

    function renderOverviewGradeFolder(grade) {
        const unreviewed = grade.unreviewedCount > 0;
        const gradeNumber = grade.name.match(/\d+/)?.[0] || 'LI';
        return `<button class="overview-grade-folder${unreviewed ? ' has-unreviewed' : ''}" type="button"
            data-open-grade="${grade.id}" aria-label="Open ${escapeHtml(grade.name)} reports; ${grade.reportCount} reports${unreviewed ? `, ${grade.unreviewedCount} unreviewed` : ''}">
            <span class="overview-folder-icon" aria-hidden="true"><span>${escapeHtml(gradeNumber)}</span></span>
            <span class="overview-folder-copy"><strong>${escapeHtml(grade.name)}</strong>
                <small>${grade.reportCount} ${grade.reportCount === 1 ? 'report' : 'reports'}</small></span>
            ${unreviewed ? `<span class="grade-notification-dot" title="${grade.unreviewedCount} unreviewed reports" aria-label="${grade.unreviewedCount} unreviewed reports"></span>` : ''}
            <span class="overview-folder-arrow" aria-hidden="true">↗</span>
        </button>`;
    }

    async function loadAdminOverview() {
        const data = await request(`/api/admin/overview?page=${overviewPage}&pageSize=10`);
        overviewPage = data.page;
        byId('overview-total').textContent = data.total;
        byId('overview-pending').textContent = data.pending + data.acknowledged;
        byId('overview-progress').textContent = data.inProgress;
        byId('overview-resolved').textContent = data.resolved;
        byId('overview-grades').innerHTML = data.gradeFolders.length
            ? data.gradeFolders.map(renderOverviewGradeFolder).join('')
            : '<p class="empty-state">No Grade Levels are currently available. Add them in Settings.</p>';
        const firstItem = data.totalReports ? (data.page - 1) * data.pageSize + 1 : 0;
        const lastItem = Math.min(data.page * data.pageSize, data.totalReports);
        const pagination = data.totalReports
            ? `<div class="table-pagination"><span>Showing ${firstItem}–${lastItem} of ${data.totalReports}</span>
                <div><button type="button" data-overview-page="${data.page - 1}" ${data.page <= 1 ? 'disabled' : ''}>Previous</button>
                <span>Page ${data.page} of ${data.totalPages}</span>
                <button type="button" data-overview-page="${data.page + 1}" ${data.page >= data.totalPages ? 'disabled' : ''}>Next</button></div></div>`
            : '';
        byId('overview-reports').innerHTML = data.reports.length
            ? `${renderAdminReportTable(data.reports, true)}${pagination}`
            : '<p class="empty-state">No reports have been submitted yet.</p>';
    }

    async function loadClassDirectory() {
        const directory = await request('/api/admin/class-options');
        classes = directory.grades || [];
        const list = byId('class-options-list');
        if (list) {
            list.innerHTML = classes.length
                ? `<div class="class-option-directory">${classes.map(grade => `<section class="class-option-grade${grade.isActive ? '' : ' is-archived'}">
                    <div class="class-option-grade-heading"><div><span class="eyebrow">GRADE LEVEL${grade.isActive ? '' : ' · ARCHIVED'}</span><h3>${escapeHtml(grade.name)}</h3></div>
                        <div class="folder-card-menu">${folderMenu('grade', grade.id, grade.name, grade.isActive)}</div></div>
                    <div class="class-option-sections">${grade.sections.length ? grade.sections.map(section => `<div class="class-option-section${section.isActive ? '' : ' is-archived'}">
                        <span>${escapeHtml(section.name)}${section.isActive ? '' : ' · Archived'}</span>
                        <div class="folder-card-menu">${folderMenu('section', section.id, section.name, section.isActive)}</div>
                    </div>`).join('') : '<p class="class-options-help">No sections yet.</p>'}</div>
                    </section>`).join('')}</div>`
                : emptyState('No class options', 'Import a workbook or create a Grade Level and Section to make class options available.');
        }
        return classes;
    }

    let classImportPreview = null;

    function renderClassImportPreview(preview) {
        const formatNames = entries => entries.length
            ? `<ul>${entries.map(entry => `<li>${escapeHtml(entry)}</li>`).join('')}</ul>`
            : '<p class="label-muted">None</p>';
        const formatSections = entries => formatNames(entries.map(entry =>
            `${entry.gradeLevel} · ${entry.section}`));
        byId('class-import-diff').innerHTML = `
            <div class="import-change-grid">
                <section><h3>Grade levels to add (${preview.gradesToAdd.length})</h3>${formatNames(preview.gradesToAdd)}</section>
                <section><h3>Grade levels to update (${preview.gradesToRestore.length})</h3>${formatNames(preview.gradesToRestore)}</section>
                <section><h3>Grade levels to remove from dropdowns (${preview.gradesToRemove.length})</h3>${formatNames(preview.gradesToRemove)}</section>
                <section><h3>Sections to add (${preview.sectionsToAdd.length})</h3>${formatSections(preview.sectionsToAdd)}</section>
                <section><h3>Sections to update (${preview.sectionsToRestore.length})</h3>${formatSections(preview.sectionsToRestore)}</section>
                <section><h3>Sections to remove from dropdowns (${preview.sectionsToRemove.length})</h3>${formatSections(preview.sectionsToRemove)}</section>
            </div>
            <p class="import-unchanged">${preview.unchangedSections} section options will remain active. Archived choices are removed only from dropdowns; existing assignments and reports are preserved.</p>`;
    }

    function setupClassOptionsImport() {
        byId('class-import-file').addEventListener('change', () => {
            classImportPreview = null;
            byId('class-import-preview').hidden = true;
            byId('class-import-error').textContent = '';
        });
        byId('class-import-form').addEventListener('submit', async event => {
            event.preventDefault();
            const button = byId('preview-class-import');
            byId('class-import-error').textContent = '';
            byId('class-import-confirm-error').textContent = '';
            byId('class-import-preview').hidden = true;
            classImportPreview = null;
            button.disabled = true;
            try {
                const file = byId('class-import-file').files[0];
                if (!file) throw new Error('Choose an .xlsx workbook to preview.');
                const formData = new FormData();
                formData.append('file', file);
                const preview = await request('/api/admin/class-options/import/preview', {
                    method: 'POST',
                    body: formData
                });
                classImportPreview = preview;
                renderClassImportPreview(preview);
                byId('class-import-preview').hidden = false;
            } catch (error) {
                byId('class-import-error').textContent = error.message;
            } finally {
                button.disabled = false;
            }
        });
        byId('confirm-class-import').addEventListener('click', async event => {
            if (!classImportPreview) return;
            const button = event.currentTarget;
            button.disabled = true;
            byId('class-import-confirm-error').textContent = '';
            try {
                const result = await request('/api/admin/class-options/import', {
                    method: 'POST',
                    body: JSON.stringify({
                        rows: classImportPreview.rows,
                        snapshot: classImportPreview.snapshot
                    })
                });
                classImportPreview = null;
                byId('class-import-preview').hidden = true;
                byId('class-import-form').reset();
                await loadClassDirectory();
                showMessage(result.message);
            } catch (error) {
                byId('class-import-confirm-error').textContent = error.message;
            } finally {
                button.disabled = false;
            }
        });
        byId('cancel-class-import').addEventListener('click', () => {
            classImportPreview = null;
            byId('class-import-preview').hidden = true;
            byId('class-import-confirm-error').textContent = '';
        });
    }

    async function showAdminPanel(name) {
        document.querySelectorAll('[data-panel]').forEach(panel => { panel.hidden = panel.dataset.panel !== name; });
        document.querySelectorAll('[data-view]').forEach(button => button.classList.toggle('is-active', button.dataset.view === name));
        activeFolder = name;
        if (name === 'overview') await loadAdminOverview();
        if (name === 'reports' && activeGradeId) await loadGradeReports();
        if (name === 'settings') await loadClassDirectory();
    }

    async function loadAdmin() {
        await loadClassDirectory();
        updateIdentity();
        await showAdminPanel('overview');
        if (adminRefreshTimer) window.clearInterval(adminRefreshTimer);
        adminRefreshTimer = window.setInterval(() => {
            const refresh = document.querySelector('[data-panel]:not([hidden])')?.dataset.panel === 'reports'
                ? (activeGradeId ? loadGradeReports() : Promise.resolve())
                : loadAdminOverview();
            refresh.catch(error => showMessage(error.message, true));
        }, 30000);
    }

    function setupAccountMenu() {
        const toggle = byId('profile-toggle');
        if (!toggle) return;
        toggle.addEventListener('click', () => {
            const menu = byId('profile-menu');
            const expanded = toggle.getAttribute('aria-expanded') === 'true';
            toggle.setAttribute('aria-expanded', String(!expanded));
            menu.hidden = expanded;
        });
        document.addEventListener('click', event => {
            if (!event.target.closest('.account-menu')) {
                byId('profile-menu').hidden = true;
                toggle.setAttribute('aria-expanded', 'false');
            }
        });
        document.querySelector('[data-open-settings]')?.addEventListener('click', async () => {
            byId('profile-menu').hidden = true;
            toggle.setAttribute('aria-expanded', 'false');
            byId('settings-error').textContent = '';
            byId('settings-dialog').showModal();
            if (!isAdmin && byId('class-settings-form')) {
                byId('class-settings-error').textContent = '';
                byId('class-settings-status').textContent = 'Refreshing available class options…';
                try {
                    const directory = await request('/api/classes');
                    classes = directory.grades || [];
                    populateClassSettings();
                } catch (error) {
                    byId('class-settings-status').textContent = 'Class options could not be refreshed.';
                    byId('class-settings-error').textContent = error.message;
                }
            }
        });
        document.querySelector('[data-logout]')?.addEventListener('click', async event => {
            const button = event.currentTarget;
            button.disabled = true;
            try {
                await request('/api/logout', { method: 'POST' });
                window.location.replace('index.html');
            } catch (error) {
                showMessage(error.message, true);
                button.disabled = false;
            }
        });
        document.querySelectorAll('[data-close-dialog]').forEach(button => {
            button.addEventListener('click', () => button.closest('dialog').close());
        });
        document.querySelectorAll('.app-dialog').forEach(dialog => dialog.addEventListener('click', event => {
            if (event.target === dialog) dialog.close();
        }));
    }

    function setupReportActions() {
        document.addEventListener('click', event => {
            if (!event.target.closest('[data-open-report]')) return;
            byId('report-error').textContent = '';
            byId('report-dialog').showModal();
        });

        const imageInput = byId('report-image');
        imageInput?.addEventListener('change', () => {
            const preview = byId('image-preview');
            preview.replaceChildren();
            const file = imageInput.files[0];
            if (!file) return;
            if (!['image/jpeg', 'image/png', 'image/gif', 'image/webp'].includes(file.type) || file.size > 1_048_576) {
                byId('report-error').textContent = 'Choose a JPEG, PNG, GIF, or WebP image smaller than 1 MB.';
                imageInput.value = '';
                return;
            }
            const image = document.createElement('img');
            image.alt = 'Selected maintenance issue photo preview';
            image.src = URL.createObjectURL(file);
            preview.append(image);
            const remove = document.createElement('button');
            remove.type = 'button';
            remove.textContent = 'Remove photo';
            remove.addEventListener('click', () => { imageInput.value = ''; preview.replaceChildren(); });
            preview.append(remove);
        });

        byId('report-form')?.addEventListener('submit', async event => {
            event.preventDefault();
            const form = event.currentTarget;
            const submit = form.querySelector('[type="submit"]');
            const file = imageInput.files[0];
            byId('report-error').textContent = '';
            submit.disabled = true;
            try {
                const image = file ? await new Promise((resolve, reject) => {
                    const reader = new FileReader();
                    reader.addEventListener('load', () => resolve(reader.result));
                    reader.addEventListener('error', () => reject(new Error('The image could not be read.')));
                    reader.readAsDataURL(file);
                }) : null;
                const fields = new FormData(form);
                const result = await request('/api/reports', {
                    method: 'POST',
                    body: JSON.stringify({
                        title: fields.get('title'),
                        location: fields.get('location'),
                        category: fields.get('category'),
                        description: fields.get('description'),
                        image
                    })
                });
                form.reset();
                byId('image-preview').replaceChildren();
                byId('report-dialog').close();
                if (isAdmin) {
                    if (activeFolder === 'reports') await loadGradeReports();
                    await loadAdminOverview();
                    showMessage(`Report FI-${String(result.id).padStart(6, '0')} created.`);
                } else {
                    reports = await request('/api/reports');
                    renderStudentReports();
                    showMessage(`Report FI-${String(result.id).padStart(6, '0')} submitted. It is now in My Reports.`);
                    await loadStudentNotifications();
                }
            } catch (error) {
                byId('report-error').textContent = error.message;
            } finally {
                submit.disabled = false;
            }
        });
    }

    function setupStudentActions() {
        byId('report-search')?.addEventListener('input', renderStudentReports);
        ['status-filter', 'category-filter', 'sort-filter'].forEach(id => byId(id)?.addEventListener('change', renderStudentReports));
        byId('report-filters')?.addEventListener('submit', event => event.preventDefault());
        byId('clear-filters')?.addEventListener('click', () => {
            ['report-search', 'status-filter', 'category-filter'].forEach(id => { if (byId(id)) byId(id).value = ''; });
            if (byId('sort-filter')) byId('sort-filter').value = 'newest';
            renderStudentReports();
        });
    }

    function setupAdminActions() {
        byId('class-folder-form').addEventListener('submit', submitClassFolderDialog);
        document.querySelector('.class-management-actions')?.addEventListener('click', event => {
            const createGrade = event.target.closest('[data-create-grade]');
            if (createGrade) {
                openClassFolderDialog({ mode: 'create-grade', type: 'grade' });
                return;
            }
            const createSection = event.target.closest('[data-create-section]');
            if (!createSection) return;
            if (!classes.some(grade => grade.isActive)) {
                showMessage('Create or restore a grade level before adding sections.', true);
                return;
            }
            openClassFolderDialog({ mode: 'create-section', type: 'section' });
        });
        document.querySelectorAll('[data-view]').forEach(button => button.addEventListener('click', () => {
            showAdminPanel(button.dataset.view).catch(error => showMessage(error.message, true));
        }));
        document.addEventListener('click', event => {
            const gradeLink = event.target.closest('[data-open-grade]');
            if (!gradeLink) return;
            activeGradeId = Number(gradeLink.dataset.openGrade);
            gradeReportsPage = 1;
            showAdminPanel('reports').catch(error => showMessage(error.message, true));
        });
        byId('folder-breadcrumbs').addEventListener('click', event => {
            const button = event.target.closest('[data-folder-level]');
            if (!button) return;
            activeGradeId = null;
            showAdminPanel('overview').catch(error => showMessage(error.message, true));
        });
        byId('class-options-list').addEventListener('click', event => {
            const menuTrigger = event.target.closest('.folder-menu-trigger');
            if (menuTrigger) {
                const menu = menuTrigger.nextElementSibling;
                const opening = menu.hidden;
                byId('class-options-list').querySelectorAll('.folder-action-menu').forEach(item => { item.hidden = true; });
                byId('class-options-list').querySelectorAll('.folder-menu-trigger').forEach(item => item.setAttribute('aria-expanded', 'false'));
                menu.hidden = !opening;
                menuTrigger.setAttribute('aria-expanded', String(opening));
                return;
            }
            const manageButton = event.target.closest('[data-folder-manage]');
            if (manageButton)
                prepareFolderManagement(manageButton).catch(error => showMessage(error.message, true));
        });
        ['folder-search', 'folder-status', 'folder-category', 'folder-section', 'folder-sort'].forEach(id => {
            const input = byId(id);
            input?.addEventListener(input.tagName === 'INPUT' ? 'input' : 'change', () => {
                gradeReportsPage = 1;
                if (activeFolder === 'reports')
                    loadGradeReports().catch(error => showMessage(error.message, true));
            });
        });
        byId('folder-clear')?.addEventListener('click', () => {
            byId('folder-search').value = '';
            byId('folder-status').value = '';
            byId('folder-category').value = '';
            byId('folder-section').value = '';
            byId('folder-sort').value = 'newest';
            gradeReportsPage = 1;
            if (activeFolder === 'reports')
                loadGradeReports().catch(error => showMessage(error.message, true));
        });
        document.addEventListener('click', event => {
            const overviewPageButton = event.target.closest('[data-overview-page]');
            if (overviewPageButton && !overviewPageButton.disabled) {
                overviewPage = Number(overviewPageButton.dataset.overviewPage);
                loadAdminOverview().catch(error => showMessage(error.message, true));
                return;
            }
            const gradePageButton = event.target.closest('[data-grade-page]');
            if (gradePageButton && !gradePageButton.disabled) {
                gradeReportsPage = Number(gradePageButton.dataset.gradePage);
                loadGradeReports().catch(error => showMessage(error.message, true));
                return;
            }
            const reviewButton = event.target.closest('[data-mark-reviewed]');
            if (!reviewButton) return;
            reviewButton.disabled = true;
            request(`/api/admin/reports/${reviewButton.dataset.markReviewed}/review`, { method: 'POST' })
                .then(async result => {
                    showMessage(result.message);
                    await Promise.all([
                        activeFolder === 'reports' ? loadGradeReports() : Promise.resolve(),
                        loadAdminOverview()
                    ]);
                })
                .catch(error => showMessage(error.message, true))
                .finally(() => { reviewButton.disabled = false; });
        });
        byId('admin-report-form').addEventListener('submit', async event => {
            event.preventDefault();
            const button = event.currentTarget.querySelector('[type="submit"]');
            button.disabled = true;
            byId('admin-report-error').textContent = '';
            try {
                await request(`/api/reports/${activeReportId}`, {
                    method: 'PATCH',
                    body: JSON.stringify({
                        status: byId('admin-status').value,
                        priority: byId('admin-priority').value,
                        assignee: byId('admin-assignee').value,
                        publicUpdate: byId('admin-public-update').value,
                        adminNotes: byId('admin-notes').value
                    })
                });
                showMessage('Report updates saved.');
                await openReport(activeReportId);
                if (activeFolder === 'reports') await loadGradeReports();
                await loadAdminOverview();
            } catch (error) {
                byId('admin-report-error').textContent = error.message;
            } finally {
                button.disabled = false;
            }
        });
        byId('delete-report').addEventListener('click', async () => {
            if (!activeReportId || !window.confirm('Delete this report permanently? This action cannot be undone.')) return;
            const button = byId('delete-report');
            button.disabled = true;
            try {
                await request(`/api/reports/${activeReportId}`, { method: 'DELETE' });
                byId('report-details-dialog').close();
                showMessage('Report deleted.');
                if (activeFolder === 'reports') await loadGradeReports();
                await loadAdminOverview();
            } catch (error) {
                showMessage(error.message, true);
            } finally {
                button.disabled = false;
            }
        });
    }

    function setupSettings() {
        byId('settings-form')?.addEventListener('submit', async event => {
            event.preventDefault();
            const button = event.currentTarget.querySelector('[type="submit"]');
            button.disabled = true;
            byId('settings-error').textContent = '';
            try {
                const result = await request('/api/profile', {
                    method: 'POST',
                    body: JSON.stringify({ username: byId('settings-name').value.trim() })
                });
                session.username = result.username;
                updateIdentity();
                byId('settings-dialog').close();
                showMessage('Your profile has been updated.');
            } catch (error) {
                byId('settings-error').textContent = error.message;
            } finally {
                button.disabled = false;
            }
        });
        byId('settings-grade')?.addEventListener('change', event => {
            setSettingsSections(event.target.value);
            byId('class-settings-error').textContent = '';
        });
        byId('class-settings-form')?.addEventListener('submit', async event => {
            event.preventDefault();
            const form = event.currentTarget;
            const button = form.querySelector('[type="submit"]');
            const gradeValue = byId('settings-grade').value;
            const sectionValue = byId('settings-section').value;
            byId('class-settings-error').textContent = '';
            if (Boolean(gradeValue) !== Boolean(sectionValue)) {
                byId('class-settings-error').textContent = 'Choose both a grade level and section, or leave both as Not set.';
                return;
            }
            button.disabled = true;
            try {
                await request('/api/profile/class', {
                    method: 'PATCH',
                    body: JSON.stringify({
                        gradeLevelId: gradeValue ? Number(gradeValue) : null,
                        sectionId: sectionValue ? Number(sectionValue) : null
                    })
                });
                profile.gradeLevelId = gradeValue ? Number(gradeValue) : null;
                profile.sectionId = sectionValue ? Number(sectionValue) : null;
                const grade = classes.find(item => item.id === profile.gradeLevelId);
                profile.gradeLevel = grade?.name || null;
                profile.section = grade?.sections.find(item => item.id === profile.sectionId)?.name || null;
                populateClassSettings();
                updateClassStatus();
                showMessage(grade ? 'Your grade and section were saved.' : 'Your class assignment was cleared.');
            } catch (error) {
                byId('class-settings-error').textContent = error.message;
            } finally {
                button.disabled = false;
            }
        });
    }

    async function initialize() {
        document.querySelectorAll('.page-message').forEach(message => { message.hidden = true; });
        try {
            session = await request('/api/me');
            if (!['user', 'admin'].includes(session.role))
                throw new Error('Your account role is not recognized.');
            if (session.role !== role) {
                window.location.replace(session.role === 'admin' ? 'dashboard_admin.html' : 'dashboard_user.html');
                return;
            }
            setupAccountMenu();
            setupSettings();
            setupReportActions();
            if (isAdmin) {
                setupClassOptionsImport();
                setupAdminActions();
                await loadAdmin();
            } else {
                setupStudentActions();
                await loadStudent();
            }
        } catch (error) {
            showMessage(`Could not load your workspace: ${error.message}`, true);
        }
    }

    document.addEventListener('click', async event => {
        const open = event.target.closest('[data-action="open-report"]');
        if (open) {
            await openReport(Number(open.dataset.reportId));
            return;
        }
        const notification = event.target.closest('[data-action="open-notification"]');
        if (notification && !isAdmin) {
            try {
                await request(`/api/notifications/${notification.dataset.notificationId}/read`, { method: 'POST' });
                await openReport(Number(notification.dataset.reportId));
            } catch (error) {
                showMessage(error.message, true);
            }
        }
    });

    if (page === 'admin') byId('admin-global-message').hidden = true;
    initialize();
})();
