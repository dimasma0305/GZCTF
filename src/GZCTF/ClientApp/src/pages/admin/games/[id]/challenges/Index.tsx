import { ActionIcon, Alert, Button, Center, Checkbox, ComboboxItem, Group, Indicator, Modal, ScrollArea, Select, SimpleGrid, Stack, Text, TextInput, Title, Tooltip } from '@mantine/core'
import { useModals } from '@mantine/modals'
import { showNotification } from '@mantine/notifications'
import { mdiAlertCircleOutline, mdiCheck, mdiHammerWrench, mdiHexagonSlice6, mdiPlus, mdiRefresh, mdiTrashCanOutline } from '@mdi/js'
import { Icon } from '@mdi/react'
import { Dispatch, FC, SetStateAction, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useParams } from 'react-router'
import { BloodBonusModel } from '@Components/admin/BloodBonusModel'
import { ChallengeCreateModal } from '@Components/admin/ChallengeCreateModal'
import { ChallengeEditCard } from '@Components/admin/ChallengeEditCard'
import { WithGameEditTab } from '@Components/admin/WithGameEditTab'
import { showErrorMsg } from '@Utils/Shared'
import { ChallengeCategoryItem, ChallengeCategoryList, useChallengeCategoryLabelMap } from '@Utils/Shared'
import { useEditChallenges } from '@Hooks/useEdit'
import api, { ChallengeInfoModel, ChallengeCategory } from '@Api'

const GameChallengeEdit: FC = () => {
  const { id } = useParams()
  const numId = parseInt(id ?? '-1')

  const [createOpened, setCreateOpened] = useState(false)
  const [bonusOpened, setBonusOpened] = useState(false)
  const [category, setCategory] = useState<ChallengeCategory | null>(null)
  const challengeCategoryLabelMap = useChallengeCategoryLabelMap()
  const [disabled, setDisabled] = useState(false)

  const { t } = useTranslation()

  const { challenges, mutate } = useEditChallenges(numId)

  const filteredChallenges = category && challenges ? challenges?.filter((c) => c.category === category) : challenges

  const modals = useModals()

  // --- batch selection / delete ---
  const [selectedIds, setSelectedIds] = useState<Set<number>>(new Set())

  const filteredIds = useMemo(
    () => (filteredChallenges ?? []).map((c) => c.id).filter((x): x is number => x != null),
    [filteredChallenges],
  )
  // Only count selections that are still in the current (filtered) view.
  const visibleSelected = filteredIds.filter((id) => selectedIds.has(id))
  const allSelected = filteredIds.length > 0 && visibleSelected.length === filteredIds.length
  const someSelected = visibleSelected.length > 0 && !allSelected

  const toggleSelect = (cid: number, checked: boolean) =>
    setSelectedIds((prev) => {
      const next = new Set(prev)
      if (checked) next.add(cid)
      else next.delete(cid)
      return next
    })

  const toggleSelectAll = () =>
    setSelectedIds((prev) => {
      const next = new Set(prev)
      if (allSelected) filteredIds.forEach((id) => next.delete(id))
      else filteredIds.forEach((id) => next.add(id))
      return next
    })

  const clearSelection = () => setSelectedIds(new Set())

  // Type-to-confirm: the operator must type "delete" before the batch
  // delete fires — guards against an accidental click wiping challenges.
  const [deleteModalOpen, setDeleteModalOpen] = useState(false)
  const [confirmText, setConfirmText] = useState('')
  const canConfirmDelete = confirmText.trim().toLowerCase() === 'delete'

  const onBatchDelete = () => {
    if (visibleSelected.length === 0) return
    setConfirmText('')
    setDeleteModalOpen(true)
  }

  const performBatchDelete = async () => {
    const ids = filteredIds.filter((id) => selectedIds.has(id))
    if (ids.length === 0 || !canConfirmDelete) return
    setDeleteModalOpen(false)
    setDisabled(true)
    try {
      const results = await Promise.allSettled(
        ids.map((cid) => api.edit.editRemoveGameChallenge(numId, cid)),
      )
      const failed = results.filter((r) => r.status === 'rejected').length
      const ok = ids.length - failed
      showNotification({
        color: failed > 0 ? 'orange' : 'teal',
        message:
          failed > 0
            ? t('admin.notification.games.challenges.batch_deleted_partial', { ok, failed })
            : t('admin.notification.games.challenges.batch_deleted', { count: ok }),
        icon: <Icon path={mdiCheck} size={1} />,
      })
      clearSelection()
      mutate()
    } catch (e) {
      showErrorMsg(e, t)
    } finally {
      setDisabled(false)
    }
  }

  const onToggle = (challenge: ChallengeInfoModel, setDisabled: Dispatch<SetStateAction<boolean>>) => {
    modals.openConfirmModal({
      title: challenge.isEnabled ? t('admin.button.challenges.disable') : t('admin.button.challenges.enable'),
      children: (
        <Text size="sm">
          {challenge.isEnabled
            ? t('admin.content.games.challenges.disable', { name: challenge.title })
            : t('admin.content.games.challenges.enable', { name: challenge.title })}
        </Text>
      ),
      onConfirm: () => onConfirmToggle(challenge, setDisabled),
      confirmProps: { color: 'orange' },
    })
  }

  const onConfirmToggle = async (challenge: ChallengeInfoModel, setDisabled: Dispatch<SetStateAction<boolean>>) => {
    const numId = parseInt(id ?? '-1')
    setDisabled(true)

    try {
      await api.edit.editUpdateGameChallenge(numId, challenge.id!, {
        isEnabled: !challenge.isEnabled,
      })
      showNotification({
        color: 'teal',
        message: t('admin.notification.games.challenges.updated'),
        icon: <Icon path={mdiCheck} size={1} />,
      })
      mutate(challenges?.map((c) => (c.id === challenge.id ? { ...c, isEnabled: !challenge.isEnabled } : c)))
    } catch (e) {
      showErrorMsg(e, t)
    } finally {
      setDisabled(false)
    }
  }

  const failedBuildCount = challenges?.filter(
    (c) => c.buildStatus === 'Failed' || c.buildStatus === 'MissingDockerfile',
  ).length ?? 0

  const onBulkRebuild = () => {
    if (!numId || failedBuildCount === 0) return
    modals.openConfirmModal({
      title: t('admin.button.challenges.bulk_rebuild'),
      children: (
        <Text size="sm">
          {t('admin.content.games.challenges.bulk_rebuild_confirm', { count: failedBuildCount })}
        </Text>
      ),
      onConfirm: async () => {
        setDisabled(true)
        try {
          const resp = await api.admin.adminBulkRebuildFailed(numId)
          showNotification({
            color: 'teal',
            message: t('admin.notification.builds.bulk_enqueued', { count: resp.data.enqueued }),
            icon: <Icon path={mdiCheck} size={1} />,
          })
          mutate()
        } catch (e) {
          showErrorMsg(e, t)
        } finally {
          setDisabled(false)
        }
      },
      confirmProps: { color: 'orange' },
    })
  }

  const onFlushScoreboard = async () => {
    if (!numId) return

    setDisabled(true)

    try {
      await api.edit.editFlushScoreboardCache(numId)
      showNotification({
        color: 'teal',
        message: t('admin.notification.games.info.scoreboard_flushed'),
        icon: <Icon path={mdiCheck} size={1} />,
      })
      mutate()
    } catch (e) {
      showErrorMsg(e, t)
    } finally {
      setDisabled(false)
    }
  }

  return (
    <WithGameEditTab
      headProps={{ justify: 'apart' }}
      isLoading={!challenges}
      head={
        <>
          <Group gap="sm" wrap="nowrap">
            <Select
              placeholder={t('admin.content.show_all')}
              clearable
              searchable
              w="16rem"
              value={category}
              nothingFoundMessage={t('admin.content.nothing_found')}
              onChange={(value) => setCategory(value as ChallengeCategory | null)}
              renderOption={ChallengeCategoryItem}
              data={ChallengeCategoryList.map((cate) => {
                const data = challengeCategoryLabelMap.get(cate)
                return { value: cate, label: data?.name, ...data } as ComboboxItem
              })}
            />
            <Checkbox
              label={t('admin.button.challenges.select_all')}
              checked={allSelected}
              indeterminate={someSelected}
              disabled={filteredIds.length === 0}
              onChange={toggleSelectAll}
            />
            {/* Batch actions live with the selection controls (left) so the
                right-side buttons — New Challenge etc. — never shift when
                they appear. */}
            {visibleSelected.length > 0 && (
              <>
                <Tooltip
                  label={`${t('admin.button.challenges.delete_selected')} (${visibleSelected.length})`}
                  withArrow
                >
                  <Indicator label={visibleSelected.length} size={16} color="red">
                    <ActionIcon
                      size="lg"
                      color="red"
                      variant="light"
                      disabled={disabled}
                      onClick={onBatchDelete}
                      aria-label={t('admin.button.challenges.delete_selected')}
                    >
                      <Icon path={mdiTrashCanOutline} size={0.9} />
                    </ActionIcon>
                  </Indicator>
                </Tooltip>
                <Button size="sm" variant="subtle" color="gray" disabled={disabled} onClick={clearSelection}>
                  {t('admin.button.challenges.clear_selection')}
                </Button>
              </>
            )}
          </Group>
          <Group justify="right" wrap="nowrap">
            {failedBuildCount > 0 && (
              <Button
                leftSection={<Icon path={mdiHammerWrench} size={1} />}
                variant="default"
                color="orange"
                disabled={disabled}
                onClick={onBulkRebuild}
              >
                {t('admin.button.challenges.bulk_rebuild')} ({failedBuildCount})
              </Button>
            )}
            <Button leftSection={<Icon path={mdiRefresh} size={1} />} disabled={disabled} onClick={onFlushScoreboard}>
              {t('admin.button.challenges.flush_scoreboard')}
            </Button>
            <Button leftSection={<Icon path={mdiHexagonSlice6} size={1} />} onClick={() => setBonusOpened(true)}>
              {t('admin.button.challenges.bonus')}
            </Button>
            <Button mr="18px" leftSection={<Icon path={mdiPlus} size={1} />} onClick={() => setCreateOpened(true)}>
              {t('admin.button.challenges.new')}
            </Button>
          </Group>
        </>
      }
    >
      <ScrollArea h="calc(100vh - 180px)" pos="relative" offsetScrollbars type="auto">
        {!filteredChallenges || filteredChallenges.length === 0 ? (
          <Center h="calc(100vh - 200px)">
            <Stack gap={0}>
              <Title order={2}>{t('admin.content.games.challenges.empty.title')}</Title>
              <Text>{t('admin.content.games.challenges.empty.description')}</Text>
            </Stack>
          </Center>
        ) : (
          <SimpleGrid pr={6} cols={{ base: 2, w18: 3, w24: 4, w30: 5, w36: 6, w42: 7, w48: 8 }} spacing="sm">
            {filteredChallenges &&
              filteredChallenges.map((challenge) => (
                <ChallengeEditCard
                  key={challenge.id}
                  challenge={challenge}
                  onToggle={onToggle}
                  onMutate={() => mutate()}
                  selectable
                  selected={challenge.id != null && selectedIds.has(challenge.id)}
                  onSelectChange={(checked) => challenge.id != null && toggleSelect(challenge.id, checked)}
                />
              ))}
          </SimpleGrid>
        )}
      </ScrollArea>
      <ChallengeCreateModal
        title={t('admin.button.challenges.new')}
        size="30%"
        opened={createOpened}
        onClose={() => setCreateOpened(false)}
        onAddChallenge={(challenge) => mutate([challenge, ...(challenges ?? [])])}
      />
      <BloodBonusModel
        title={t('admin.button.challenges.bonus')}
        size="30%"
        opened={bonusOpened}
        onClose={() => setBonusOpened(false)}
      />
      <Modal
        opened={deleteModalOpen}
        onClose={() => setDeleteModalOpen(false)}
        title={t('admin.button.challenges.delete_selected')}
        centered
      >
        <Stack gap="sm">
          <Alert color="red" variant="light" icon={<Icon path={mdiAlertCircleOutline} size={1} />}>
            {t('admin.content.games.challenges.delete_selected_confirm', { count: visibleSelected.length })}
          </Alert>
          <Text size="sm">{t('admin.content.games.challenges.delete_type_to_confirm')}</Text>
          <TextInput
            value={confirmText}
            onChange={(e) => setConfirmText(e.currentTarget.value)}
            placeholder="delete"
            data-autofocus
            autoComplete="off"
            onKeyDown={(e) => {
              if (e.key === 'Enter' && canConfirmDelete) performBatchDelete()
            }}
          />
          <Group justify="flex-end" gap="sm">
            <Button variant="default" onClick={() => setDeleteModalOpen(false)}>
              {t('common.modal.cancel', 'Cancel')}
            </Button>
            <Button
              color="red"
              leftSection={<Icon path={mdiTrashCanOutline} size={0.9} />}
              disabled={!canConfirmDelete || disabled}
              onClick={performBatchDelete}
            >
              {t('admin.button.challenges.delete_selected')} ({visibleSelected.length})
            </Button>
          </Group>
        </Stack>
      </Modal>
    </WithGameEditTab>
  )
}

export default GameChallengeEdit
